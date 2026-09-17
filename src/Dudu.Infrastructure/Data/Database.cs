using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Dudu.Infrastructure.Data;

public sealed class Database : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, DatabaseAccessCoordinator> Coordinators = new(StringComparer.OrdinalIgnoreCase);
    private readonly DatabaseOptions _options;
    private readonly DatabaseAccessCoordinator _coordinator;

    public Database(DatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _coordinator = Coordinators.GetOrAdd(
            Path.GetFullPath(_options.DatabasePath),
            static _ => new DatabaseAccessCoordinator());
    }

    public DatabaseOptions Options => _options;

    public int InitializationRunCount => _coordinator.InitializationRunCount;

    public static async Task<Database> OpenAsync(
        DatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        var database = new Database(options);
        await database.InitializeAsync(cancellationToken);
        return database;
    }

    public static Task<Database> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        OpenAsync(new DatabaseOptions(databasePath), cancellationToken);

    public static Task<Database> OpenAsync(
        string databasePath,
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        OpenAsync(new DatabaseOptions(databasePath, backupDirectory), cancellationToken);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // InitializeCoreAsync deliberately runs with its own uncancelled
        // ownership. Do not let a caller abandon the await while the shared
        // coordinator continues mutating the database; callers can cancel
        // before initialization begins, and connection operations remain
        // independently cancellable after initialization completes.
        cancellationToken.ThrowIfCancellationRequested();
        await _coordinator.InitializeAsync(InitializeCoreAsync);
    }

    internal void InvalidateInitialization()
    {
        _coordinator.ResetInitialization();
    }

    public async Task<SqliteConnection> CreateConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await OpenConnectionAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Microsoft.Data.Sqlite pools connections per connection string and keeps the
    // database file open until the pool is cleared; on Windows that blocks deleting
    // or moving the data root after the owning host has shut down.
    public void Dispose() => SqliteConnection.ClearAllPools();

    internal static string ConnectionString(DatabaseOptions options) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, options.BusyTimeoutMilliseconds / 1000),
        }.ToString();

    internal static void ConfigureConnection(SqliteConnection connection)
    {
        using var busyTimeout = connection.CreateCommand();
        busyTimeout.CommandText = "PRAGMA busy_timeout=5000;";
        busyTimeout.ExecuteNonQuery();

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
        foreignKeys.ExecuteNonQuery();

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        journalMode.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await _coordinator.Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = new SqliteConnection(ConnectionString(_options));
            await connection.OpenAsync(cancellationToken);
            ConfigureConnection(connection);
            _coordinator.Track(connection);
            return connection;
        }
        finally
        {
            _coordinator.Gate.Release();
        }
    }

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
            ?? throw new InvalidOperationException("The database path has no directory."));
        Directory.CreateDirectory(_options.BackupDirectory);

        // A previous process may have died mid-restore with the canonical path missing or
        // holding a partial file. Reconciliation must run before this create-mode open,
        // otherwise SQLite would silently seed an empty database over the recovery set.
        await new DatabaseBackupService(_options).ReconcileInterruptedRestoreAsync(CancellationToken.None);

        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await new MigrationRunner(_options).RunAsync(connection, CancellationToken.None);
        await SeedData.SeedAsync(connection, CancellationToken.None);
    }

    internal Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken) =>
        _coordinator.EnterMaintenanceAsync(cancellationToken);
}

internal sealed class DatabaseAccessCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<SqliteConnection> _connections = [];
    private TaskCompletionSource<bool> _connectionsDrained = CompletedSource();
    private TaskCompletionSource<bool>? _initializationSource;
    private int _initializationRunCount;
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public int InitializationRunCount => Volatile.Read(ref _initializationRunCount);

    public Task InitializeAsync(Func<Task> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        lock (_sync)
        {
            if (_initializationSource is not null)
            {
                return _initializationSource.Task;
            }

            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializationSource = source;
            _initializationRunCount++;
            _ = RunInitializationAsync(source, initializer);
            return source.Task;
        }
    }

    public void ResetInitialization()
    {
        lock (_sync)
        {
            _initializationSource = null;
        }
    }

    public void Track(SqliteConnection connection)
    {
        lock (_sync)
        {
            _connections.Add(connection);
            if (_connections.Count == 1)
            {
                _connectionsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        connection.StateChange += OnStateChange;
    }

    public async Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Task drained;
            lock (_sync)
            {
                drained = _connections.Count == 0
                    ? Task.CompletedTask
                    : _connectionsDrained.Task;
            }

            await drained.WaitAsync(cancellationToken);
            SqliteConnection.ClearAllPools();

            return new MaintenanceLease(Gate);
        }
        catch
        {
            Gate.Release();
            throw;
        }
    }

    private void OnStateChange(object? sender, System.Data.StateChangeEventArgs args)
    {
        if (args.CurrentState == System.Data.ConnectionState.Closed && sender is SqliteConnection connection)
        {
            lock (_sync)
            {
                if (_connections.Remove(connection) && _connections.Count == 0)
                {
                    _connectionsDrained.TrySetResult(true);
                }
            }
        }
    }

    private async Task RunInitializationAsync(
        TaskCompletionSource<bool> source,
        Func<Task> initializer)
    {
        try
        {
            await initializer();
            source.TrySetResult(true);
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_initializationSource, source))
                {
                    _initializationSource = null;
                }
            }

            source.TrySetException(exception);
        }
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private sealed class MaintenanceLease(SemaphoreSlim gate) : IDisposable
    {
        private readonly SemaphoreSlim _gate = gate;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
            }
        }
    }
}
