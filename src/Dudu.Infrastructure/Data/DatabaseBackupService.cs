using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public enum RestoreFailure
{
    None,
    NotFound,
    IntegrityCheckFailed,
    RestoreFailed,
}

public sealed record RestoreResult(
    bool Restored,
    RestoreFailure Failure,
    string? BackupPath = null,
    Exception? Exception = null);

public sealed class DatabaseBackupService
{
    private readonly DatabaseOptions _options;

    public DatabaseBackupService(DatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public DatabaseBackupService(string databasePath, string? backupDirectory = null)
        : this(new DatabaseOptions(databasePath, backupDirectory))
    {
    }

    public async Task<string?> CreatePreMigrationBackupAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.DatabasePath))
        {
            return null;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _options.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);
        return await CreatePreMigrationBackupAsync(connection, cancellationToken);
    }

    public async Task<string> CreatePreMigrationBackupAsync(
        SqliteConnection source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(_options.BackupDirectory);
        var path = NewBackupPath();

        await using (var command = source.CreateCommand())
        {
            // VACUUM INTO copies the live database, including committed WAL data,
            // without copying transient -wal/-shm files.
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsValidBackupAsync(path, cancellationToken))
        {
            throw new InvalidDataException($"SQLite backup failed its integrity check: {path}");
        }

        await RotateAsync(cancellationToken);
        return path;
    }

    public Task<RestoreResult> TryRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default) =>
        RestoreAsync(backupPath, cancellationToken);

    public async Task<RestoreResult> RestoreLatestValidAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = Directory.Exists(_options.BackupDirectory)
            ? Directory.GetFiles(_options.BackupDirectory, "*.db")
                .OrderByDescending(File.GetLastWriteTimeUtc)
            : Enumerable.Empty<string>();

        var found = false;
        foreach (var candidate in candidates)
        {
            found = true;
            var result = await RestoreAsync(candidate, cancellationToken);
            if (result.Restored)
            {
                return result;
            }
        }

        return new RestoreResult(
            false,
            found ? RestoreFailure.IntegrityCheckFailed : RestoreFailure.NotFound);
    }

    public async Task<bool> IsValidBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            return false;
        }

        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            return string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task<RestoreResult> RestoreAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath))
        {
            return new RestoreResult(false, RestoreFailure.NotFound, backupPath);
        }

        if (!await IsValidBackupAsync(backupPath, cancellationToken))
        {
            return new RestoreResult(false, RestoreFailure.IntegrityCheckFailed, backupPath);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
                ?? throw new InvalidOperationException("The database path has no directory."));
            var temporaryPath = _options.DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(backupPath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, _options.DatabasePath, overwrite: true);
            return new RestoreResult(true, RestoreFailure.None, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RestoreResult(false, RestoreFailure.RestoreFailed, backupPath, exception);
        }
    }

    private async Task RotateAsync(CancellationToken cancellationToken)
    {
        var valid = new List<string>();
        foreach (var path in Directory.GetFiles(_options.BackupDirectory, "*.db")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (await IsValidBackupAsync(path, cancellationToken))
            {
                valid.Add(path);
            }
        }

        foreach (var path in valid.Skip(Math.Max(0, _options.BackupRetentionCount)))
        {
            File.Delete(path);
        }
    }

    private string NewBackupPath() => Path.Combine(
        _options.BackupDirectory,
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
}
