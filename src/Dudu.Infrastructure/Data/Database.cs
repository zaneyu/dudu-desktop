using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public sealed class Database : IAsyncDisposable, IDisposable
{
    private readonly DatabaseOptions _options;
    private int _opened;

    public Database(DatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        if (Interlocked.Exchange(ref _opened, 1) == 1)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
                ?? throw new InvalidOperationException("The database path has no directory."));
            Directory.CreateDirectory(_options.BackupDirectory);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await new MigrationRunner(_options).RunAsync(connection, cancellationToken);
            await SeedData.SeedAsync(connection, cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _opened, 0);
            throw;
        }
    }

    public async Task<SqliteConnection> CreateConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _opened) == 0)
        {
            await InitializeAsync(cancellationToken);
        }

        return await OpenConnectionAsync(cancellationToken);
    }

    public SqliteConnection CreateConnection()
    {
        if (Volatile.Read(ref _opened) == 0)
        {
            throw new InvalidOperationException("The database must be opened before creating a connection.");
        }

        var connection = new SqliteConnection(ConnectionString(_options));
        connection.Open();
        ConfigureConnection(connection);
        return connection;
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
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(ConnectionString(_options));
        await connection.OpenAsync(cancellationToken);
        ConfigureConnection(connection);
        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        await journalMode.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
