using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Dudu.Infrastructure.Data;

public sealed class Database : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, DatabaseAccessCoordinator> Coordinators = new(StringComparer.OrdinalIgnoreCase);
    private readonly DatabaseOptions _options;
    private readonly DatabaseAccessCoordinator _coordinator;
    private readonly object _initializationSync = new();
    private Task? _initializationTask;

    public Database(DatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _coordinator = Coordinators.GetOrAdd(
            Path.GetFullPath(_options.DatabasePath),
            static path => new DatabaseAccessCoordinator(path));
    }

    public DatabaseOptions Options => _options;

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
        Task initialization;
        lock (_initializationSync)
        {
            _initializationTask ??= InitializeCoreAsync();
            initialization = _initializationTask;
        }

        try
        {
            await initialization.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_initializationSync)
            {
                if (_initializationTask is { IsCompleted: true })
                {
                    _initializationTask = null;
                }
            }
            throw;
        }
    }

    public async Task<SqliteConnection> CreateConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await OpenConnectionAsync(cancellationToken);
    }

    public SqliteConnection CreateConnection()
    {
        InitializeAsync().GetAwaiter().GetResult();
        return OpenConnection();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }

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

    private SqliteConnection OpenConnection()
    {
        _coordinator.Gate.Wait();
        try
        {
            var connection = new SqliteConnection(ConnectionString(_options));
            connection.Open();
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

        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await new MigrationRunner(_options).RunAsync(connection, CancellationToken.None);
        await SeedData.SeedAsync(connection, CancellationToken.None);
    }

    internal Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken) =>
        _coordinator.EnterMaintenanceAsync(cancellationToken);
}

internal sealed class DatabaseAccessCoordinator(string databasePath)
{
    private readonly string _databasePath = databasePath;
    private readonly object _sync = new();
    private readonly HashSet<SqliteConnection> _connections = [];
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Track(SqliteConnection connection)
    {
        lock (_sync)
        {
            _connections.Add(connection);
        }

        connection.StateChange += OnStateChange;
    }

    public async Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            SqliteConnection[] connections;
            lock (_sync)
            {
                connections = _connections.ToArray();
                _connections.Clear();
            }

            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }

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
                _connections.Remove(connection);
            }
        }
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
