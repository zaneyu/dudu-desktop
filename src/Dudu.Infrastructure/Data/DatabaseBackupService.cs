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
    private readonly Database _database;
    private readonly Action<string, string> _moveFile;
    private readonly Action<string> _deleteFile;
    private readonly Action? _beforeStaging;

    public DatabaseBackupService(DatabaseOptions options)
        : this(options, null)
    {
    }

    public DatabaseBackupService(
        DatabaseOptions options,
        Action<string, string>? moveFile,
        Action<string>? deleteFile = null,
        Action? beforeStaging = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _database = new Database(_options);
        _moveFile = moveFile ?? ((source, destination) =>
            File.Move(source, destination, overwrite: true));
        _deleteFile = deleteFile ?? File.Delete;
        _beforeStaging = beforeStaging;
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

        if (!await IsSqliteBackupAsync(path, cancellationToken))
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
        // Backup filenames embed the creation timestamp (yyyyMMddHHmmssfff-guid), so ordering by
        // filename is ordering by creation time. File mtimes are deliberately not used: copies,
        // restores, and coarse filesystem timestamp granularity can all make mtime disagree with
        // which backup is actually newest.
        var candidates = Directory.Exists(_options.BackupDirectory)
            ? Directory.GetFiles(_options.BackupDirectory, "*.db")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
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
                    // Pooled connections keep the backup file open after Dispose; on Windows that
                    // blocks the File.Delete in RotateAsync and LocalDataMaintenanceService.
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            if (!await IsSqliteIntegrityOkAsync(connection, cancellationToken))
            {
                return false;
            }

            var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
            var currentSchemaVersion = new MigrationRunner(_options).Migrations.Max(migration => migration.Version);
            // Backups from an older schema are accepted: the next startup migrates them forward.
            // Only backups from a newer (unknown) schema — or with no schema version at all — are
            // rejected, since the current migrations cannot interpret them.
            return schemaVersion.HasValue && schemaVersion.Value <= currentSchemaVersion;
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
            using var maintenance = await _database.EnterMaintenanceAsync(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
                ?? throw new InvalidOperationException("The database path has no directory."));
            await CheckpointCurrentDatabaseAsync(cancellationToken);
            var temporaryPath = _options.DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");
            var stagedMainPath = _options.DatabasePath + ".restore-old-" + Guid.NewGuid().ToString("N");
            var stagedWalPath = stagedMainPath + "-wal";
            var stagedShmPath = stagedMainPath + "-shm";
            var stagedFiles = new List<(string CanonicalPath, string StagedPath)>();
            var installAttempted = false;
            try
            {
                _beforeStaging?.Invoke();
                File.Copy(backupPath, temporaryPath, overwrite: false);
                StageFile(_options.DatabasePath, stagedMainPath, stagedFiles);
                StageFile(_options.DatabasePath + "-wal", stagedWalPath, stagedFiles);
                StageFile(_options.DatabasePath + "-shm", stagedShmPath, stagedFiles);

                installAttempted = true;
                _moveFile(temporaryPath, _options.DatabasePath);
                CleanupStagedFiles(stagedFiles);
                return new RestoreResult(true, RestoreFailure.None, backupPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Exception resultException = exception;
                try
                {
                    if (installAttempted && File.Exists(_options.DatabasePath))
                    {
                        File.Move(
                            _options.DatabasePath,
                            _options.DatabasePath + ".restore-failed-" + Guid.NewGuid().ToString("N"));
                    }

                    foreach (var (_, stagedPath) in stagedFiles.AsEnumerable().Reverse())
                    {
                        if (File.Exists(stagedPath))
                        {
                            var canonicalPath = stagedFiles.First(item => item.StagedPath == stagedPath).CanonicalPath;
                            File.Move(stagedPath, canonicalPath);
                        }
                    }
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    resultException = new AggregateException(exception, rollbackException);
                }

                return new RestoreResult(false, RestoreFailure.RestoreFailed, backupPath, resultException);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    TryDelete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new RestoreResult(false, RestoreFailure.RestoreFailed, backupPath, exception);
        }
    }

    private async Task RotateAsync(CancellationToken cancellationToken)
    {
        var valid = new List<string>();
        foreach (var path in Directory.GetFiles(_options.BackupDirectory, "*.db")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            if (await IsSqliteBackupAsync(path, cancellationToken))
            {
                valid.Add(path);
            }
        }

        foreach (var path in valid.Skip(Math.Max(0, _options.BackupRetentionCount)))
        {
            try
            {
                if (File.Exists(path))
                {
                    _deleteFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Rotation is best effort: a locked or unreadable file must not fail the backup
                // that was just created. The next rotation retries the leftover file.
            }
        }
    }

    private string NewBackupPath() => Path.Combine(
        _options.BackupDirectory,
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");

    private void StageFile(
        string canonicalPath,
        string stagedPath,
        ICollection<(string CanonicalPath, string StagedPath)> stagedFiles)
    {
        if (!File.Exists(canonicalPath))
        {
            return;
        }

        stagedFiles.Add((canonicalPath, stagedPath));
        _moveFile(canonicalPath, stagedPath);
    }

    private void CleanupStagedFiles(
        IEnumerable<(string CanonicalPath, string StagedPath)> stagedFiles)
    {
        foreach (var (_, stagedPath) in stagedFiles)
        {
            TryDelete(stagedPath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                _deleteFile(path);
            }
        }
        catch (Exception)
        {
            // Cleanup is intentionally best effort after the new database is canonical.
        }
    }

    private async Task CheckpointCurrentDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.DatabasePath))
        {
            return;
        }

        await using var connection = new SqliteConnection(Database.ConnectionString(_options));
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private Task<bool> IsSqliteBackupAsync(
        string backupPath,
        CancellationToken cancellationToken) =>
        IsSqliteBackupCoreAsync(backupPath, cancellationToken);

    private static async Task<bool> IsSqliteBackupCoreAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath)) return false;
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            return await IsSqliteIntegrityOkAsync(connection, cancellationToken);
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
    }

    private static async Task<bool> IsSqliteIntegrityOkAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        return string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int?> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version';";
        if (Convert.ToInt32(await table.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            return null;
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_version;";
        var value = await version.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }
}
