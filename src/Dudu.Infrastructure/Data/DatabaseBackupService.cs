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
    /// <summary>Marker appended to files preserved from a corrupt pre-restore database. A
    /// canonical name would be mistaken for the database itself on the next open; the suffix
    /// keeps the recovery set inert until LocalDataMaintenanceService or reconciliation
    /// disposes of it.</summary>
    public const string RecoverySuffix = ".corrupt-recovery";

    /// <summary>
    /// The <c>StartupFailureLogger</c> phase for backup-prune and maintenance
    /// failures. See <see cref="FailureReporter"/>.
    /// </summary>
    public const string PruneFailurePhase = "backup-prune";

    /// <summary>
    /// Filename marker distinguishing a pre-migration backup -- the automatic snapshot
    /// <see cref="MigrationRunner"/> takes before the first migration in an upgrade run --
    /// from a manual or (future) scheduled one. Only a pre-migration backup is the pre-upgrade
    /// safety net <see cref="HasValidBackupAtSchemaVersionAsync"/> exists to make idempotent;
    /// a manual/scheduled backup at the same schema version was taken for an unrelated reason
    /// and must never be mistaken for it. Appended after the existing timestamp-guid name so
    /// restore, rotation, and listing -- which all just glob "*.db" -- keep working unchanged
    /// for backups written before this marker existed.
    /// </summary>
    private const string PreMigrationMarker = "-premigration";

    private readonly DatabaseOptions _options;
    private readonly Database _database;
    private readonly Action<string, string> _moveFile;
    private readonly Action<string> _deleteFile;
    private readonly Action? _beforeStaging;
    private readonly bool _invalidateInitializationOnRestore;

    public DatabaseBackupService(DatabaseOptions options)
        : this(options, null)
    {
    }

    public DatabaseBackupService(
        DatabaseOptions options,
        Action<string, string>? moveFile,
        Action<string>? deleteFile = null,
        Action? beforeStaging = null)
        : this(options, moveFile, deleteFile, beforeStaging, invalidateInitializationOnRestore: true)
    {
    }

    // Used only by Database.TryRestoreFromBackupAsync's own corruption-recovery restore: that
    // call already runs inside InitializeCoreAsync, under the same latched initialization
    // attempt that will complete it. Invalidating there would null the coordinator's
    // _initializationSource out from under the in-flight RunInitializationAsync, so the very
    // next InitializeAsync call re-runs InitializeCoreAsync and resets LastRecoveryOutcome to
    // None, erasing the record that a restore just happened. A restore triggered any other way
    // (e.g. Settings' "restore latest backup") must keep invalidating, since the canonical file
    // was replaced outside of any in-flight initialization and a later read must not skip
    // migrations/reconciliation on it.
    internal DatabaseBackupService(DatabaseOptions options, bool invalidateInitializationOnRestore)
        : this(options, null, null, null, invalidateInitializationOnRestore)
    {
    }

    private DatabaseBackupService(
        DatabaseOptions options,
        Action<string, string>? moveFile,
        Action<string>? deleteFile,
        Action? beforeStaging,
        bool invalidateInitializationOnRestore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _database = new Database(_options);
        _moveFile = moveFile ?? ((source, destination) =>
            File.Move(source, destination, overwrite: true));
        _deleteFile = deleteFile ?? File.Delete;
        _beforeStaging = beforeStaging;
        _invalidateInitializationOnRestore = invalidateInitializationOnRestore;
    }

    public DatabaseBackupService(string databasePath, string? backupDirectory = null)
        : this(new DatabaseOptions(databasePath, backupDirectory))
    {
    }

    /// <summary>
    /// Optional failure hook invoked with (<c>PruneFailurePhase</c>,
    /// exception) when a backup-prune (rotation) deletion fails. Logging
    /// only: rotation stays best-effort and the just-created backup still
    /// succeeds exactly as before. A plain delegate, not an <c>ILogger</c>,
    /// so no logging dependency enters the data layer.
    /// </summary>
    public Action<string, Exception>? FailureReporter { get; set; }

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
                // Pooled connections keep the file open after Dispose; the validation
                // connections elsewhere in this class already disable pooling for the
                // same reason. This one-shot connection (e.g. "Back up now") otherwise
                // gets a different pool key (no DefaultTimeout) than the one Database
                // clears on Dispose, so its handle could keep dudu.db locked on Windows.
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);
        // This overload backs a manual "Back up now" (and any future scheduled backup); it
        // must NOT carry the pre-migration marker, or HasValidBackupAtSchemaVersionAsync would
        // treat a manual snapshot as the pre-upgrade safety net it never was.
        return await CreateBackupAsync(connection, isPreMigration: false, cancellationToken);
    }

    /// <summary>
    /// Creates the automatic pre-upgrade snapshot. Called only by <see cref="MigrationRunner"/>,
    /// immediately before the first migration that actually applies in an upgrade run -- the
    /// resulting file carries the pre-migration marker so it, and only it, can satisfy
    /// <see cref="HasValidBackupAtSchemaVersionAsync"/>.
    /// </summary>
    public Task<string> CreatePreMigrationBackupAsync(
        SqliteConnection source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CreateBackupAsync(source, isPreMigration: true, cancellationToken);
    }

    private async Task<string> CreateBackupAsync(
        SqliteConnection source,
        bool isPreMigration,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        var path = NewBackupPath(isPreMigration);

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

    /// <summary>
    /// Whether a valid <em>pre-migration</em> backup already on disk was taken at exactly
    /// <paramref name="schemaVersion"/>. Used by <see cref="MigrationRunner"/> to make the
    /// pre-upgrade backup idempotent across repeated attempts from the same starting version
    /// (a fresh process retrying a migration that keeps failing, for example) instead of
    /// creating a fresh redundant snapshot -- and eventually evicting the genuine pre-upgrade
    /// backup -- on every attempt. Manual and (future) scheduled backups never satisfy this,
    /// even when one happens to sit at the same schema version: it was taken for an unrelated
    /// reason and is not the pre-upgrade snapshot this run needs.
    /// </summary>
    public async Task<bool> HasValidBackupAtSchemaVersionAsync(
        int schemaVersion,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_options.BackupDirectory))
        {
            return false;
        }

        foreach (var path in Directory.GetFiles(_options.BackupDirectory, "*.db"))
        {
            if (!Path.GetFileName(path).Contains(PreMigrationMarker, StringComparison.Ordinal))
            {
                continue;
            }

            if (!await IsSqliteBackupAsync(path, cancellationToken))
            {
                continue;
            }

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            if (await ReadSchemaVersionAsync(connection, cancellationToken) == schemaVersion)
            {
                return true;
            }
        }

        return false;
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
                .ToArray()
            : [];

        var failure = default(RestoreFailure?);
        foreach (var candidate in candidates)
        {
            var result = await RestoreAsync(candidate, cancellationToken);
            if (result.Restored)
            {
                return result;
            }

            failure ??= result.Failure;
        }

        return new RestoreResult(
            false,
            failure ?? RestoreFailure.NotFound);
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
            if (!await HasSqliteHeaderAsync(backupPath, cancellationToken))
            {
                return false;
            }

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
            var checkpoint = await CheckpointCurrentDatabaseAsync(cancellationToken);
            var temporaryPath = _options.DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");
            var stagedMainPath = _options.DatabasePath + ".restore-old-" + Guid.NewGuid().ToString("N");
            var stagedWalPath = stagedMainPath + "-wal";
            var stagedShmPath = stagedMainPath + "-shm";
            var stagedFiles = new List<(string CanonicalPath, string StagedPath)>();
            var installAttempted = false;
            var recoverySetKept = false;
            try
            {
                _beforeStaging?.Invoke();
                File.Copy(backupPath, temporaryPath, overwrite: false);
                StageFile(_options.DatabasePath, stagedMainPath, stagedFiles);
                StageFile(_options.DatabasePath + "-wal", stagedWalPath, stagedFiles);
                StageFile(_options.DatabasePath + "-shm", stagedShmPath, stagedFiles);

                if (checkpoint.Corrupt && stagedFiles.Count > 0)
                {
                    // The corrupt current database is left on disk as a recovery set: it is not
                    // silently destroyed, and startup reconciliation offers it back if the
                    // restored replacement is ever found missing.
                    recoverySetKept = true;
                }

                installAttempted = true;
                _moveFile(temporaryPath, _options.DatabasePath);
                await MigrateRestoredDatabaseAsync(cancellationToken);
                CleanupCanonicalSidecars();
                if (!recoverySetKept)
                {
                    CleanupStagedFiles(stagedFiles);
                }

                if (_invalidateInitializationOnRestore)
                {
                    _database.InvalidateInitialization();
                }

                return new RestoreResult(true, RestoreFailure.None, backupPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                Exception resultException = exception;
                try
                {
                    if (installAttempted)
                    {
                        SqliteConnection.ClearAllPools();
                        CleanupCanonicalSidecars();
                        if (File.Exists(_options.DatabasePath))
                        {
                            File.Move(
                                _options.DatabasePath,
                                _options.DatabasePath + ".restore-failed-" + Guid.NewGuid().ToString("N"),
                                overwrite: true);
                        }
                    }

                    foreach (var (_, stagedPath) in stagedFiles.AsEnumerable().Reverse())
                    {
                        if (File.Exists(stagedPath))
                        {
                            var canonicalPath = stagedFiles.First(item => item.StagedPath == stagedPath).CanonicalPath;
                            File.Move(stagedPath, canonicalPath, overwrite: true);
                        }
                    }

                    recoverySetKept = false;
                    if (_invalidateInitializationOnRestore)
                    {
                        _database.InvalidateInitialization();
                    }
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or SqliteException)
                {
                    resultException = new AggregateException(exception, rollbackException);
                }

                return new RestoreResult(false, RestoreFailure.RestoreFailed, backupPath, resultException);
            }
            catch when (installAttempted)
            {
                // A non-IO failure after the replacement was installed (cancellation
                // mid-migration, an unexpected throw from a seam): roll the staged
                // originals back best-effort, then drop the cached initialization
                // before rethrowing — the canonical file is already the new
                // database, and every later connection must not skip migrations
                // and reconciliation on it.
                foreach (var (_, stagedPath) in stagedFiles.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(stagedPath))
                        {
                            var canonicalPath = stagedFiles.First(item => item.StagedPath == stagedPath).CanonicalPath;
                            File.Move(stagedPath, canonicalPath, overwrite: true);
                        }
                    }
                    catch (Exception rollbackException) when (
                        rollbackException is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                if (_invalidateInitializationOnRestore)
                {
                    _database.InvalidateInitialization();
                }

                throw;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    TryDelete(temporaryPath);
                }

                if (recoverySetKept)
                {
                    foreach (var (_, stagedPath) in stagedFiles)
                    {
                        if (!File.Exists(stagedPath))
                        {
                            continue;
                        }

                        try
                        {
                            var canonicalPath = stagedFiles.First(item => item.StagedPath == stagedPath).CanonicalPath;
                            File.Move(
                                stagedPath,
                                canonicalPath + RecoverySuffix,
                                overwrite: true);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new RestoreResult(false, RestoreFailure.RestoreFailed, backupPath, exception);
        }
    }

    private async Task MigrateRestoredDatabaseAsync(CancellationToken cancellationToken)
    {
        // MigrationRunner.RunAsync backs up before each migration; on a freshly restored
        // database that would snapshot a pre-migration state into the backup set and, worse,
        // run while the caller still holds only the maintenance lease. Migrations here must
        // not recurse into backup creation.
        var runner = new MigrationRunner(
            _options);
        await using var connection = new SqliteConnection(Database.ConnectionString(_options));
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);
        await runner.RunAsync(connection, cancellationToken, createBackups: false);
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    public async Task<bool> ReconcileInterruptedRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = _options.DatabasePath;
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPaths = SafeFiles(directory, databasePath + ".restore-", excludeOld: true, excludeFailed: true);
        var stagedOldPaths = SafeFiles(directory, databasePath + ".restore-old-");
        var stagedFailedPaths = SafeFiles(directory, databasePath + ".restore-failed-");
        // Recovery-set files are named "<canonicalPath>{-wal,-shm}.corrupt-recovery"
        // (see the RestoreAsync finally block below), not
        // "<databasePath>.restore-old-*.corrupt-recovery" -- the previous prefix
        // here never matched anything, so this branch was dead code.
        var recoveryPaths = SafeFiles(directory, databasePath)
            .Where(path => path.EndsWith(RecoverySuffix, StringComparison.Ordinal))
            .ToArray();

        if (!File.Exists(databasePath))
        {
            // A crash between staging the old files aside and installing the replacement
            // leaves no canonical database. The staged-old set is the pre-restore state, so
            // the newest staged main goes back before anything can open the path in create
            // mode and silently seed an empty database.
            var stagedOld = MainFiles(stagedOldPaths)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (stagedOld is not null)
            {
                CleanupCanonicalSidecars();
                RecoverFileSet(stagedOld, stagedOldPaths, databasePath);
                CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);
                return true;
            }

            var recovered = recoveryPaths.FirstOrDefault(path =>
                !path.EndsWith("-wal" + RecoverySuffix, StringComparison.Ordinal)
                && !path.EndsWith("-shm" + RecoverySuffix, StringComparison.Ordinal));
            if (recovered is not null)
            {
                CleanupCanonicalSidecars();
                RecoverRecoverySet(recovered, recoveryPaths, databasePath);
                CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);
                return true;
            }

            return false;
        }

        if (await IsSqliteBackupCoreAsync(databasePath, cancellationToken))
        {
            CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);

            return false;
        }

        // The canonical path exists but is not a readable database: either the install was
        // interrupted mid-move (a `.restore-*` temp may hold the validated replacement) or the
        // file itself is truncated. Prefer a valid `.restore-*` temp, then a staged-old set.
        foreach (var replacement in MainFiles(temporaryPaths))
        {
            if (await IsSqliteBackupCoreAsync(replacement, cancellationToken))
            {
                CleanupCanonicalSidecars();
                _moveFile(replacement, databasePath);
                CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);

                return true;
            }
        }

        var stagedOldFallback = MainFiles(stagedOldPaths)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (stagedOldFallback is not null)
        {
            CleanupCanonicalSidecars();
            RecoverFileSet(stagedOldFallback, stagedOldPaths, databasePath);
            CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);
            return true;
        }

        var recoveredFallback = recoveryPaths.FirstOrDefault(path =>
            !path.EndsWith("-wal" + RecoverySuffix, StringComparison.Ordinal)
            && !path.EndsWith("-shm" + RecoverySuffix, StringComparison.Ordinal));
        if (recoveredFallback is not null)
        {
            CleanupCanonicalSidecars();
            RecoverRecoverySet(recoveredFallback, recoveryPaths, databasePath);
            CleanupRestoreArtifacts(temporaryPaths, stagedOldPaths, stagedFailedPaths, recoveryPaths);
            return true;
        }

        return false;
    }

    private void RecoverFileSet(
        string stagedMainPath,
        IEnumerable<string> stagedPaths,
        string canonicalMainPath)
    {
        _moveFile(stagedMainPath, canonicalMainPath);
        foreach (var sidecar in stagedPaths.Where(path =>
                     path.EndsWith("-wal", StringComparison.Ordinal)
                     || path.EndsWith("-shm", StringComparison.Ordinal)))
        {
            var canonicalSidecar = sidecar.EndsWith("-wal", StringComparison.Ordinal)
                ? canonicalMainPath + "-wal"
                : canonicalMainPath + "-shm";
            if (File.Exists(sidecar))
            {
                _moveFile(sidecar, canonicalSidecar);
            }
        }
    }

    private void RecoverRecoverySet(
        string recoveryMainPath,
        IEnumerable<string> recoveryPaths,
        string canonicalMainPath)
    {
        _moveFile(recoveryMainPath, canonicalMainPath);
        foreach (var sidecar in recoveryPaths.Where(path =>
                     path.EndsWith("-wal" + RecoverySuffix, StringComparison.Ordinal)
                     || path.EndsWith("-shm" + RecoverySuffix, StringComparison.Ordinal)))
        {
            var canonicalSidecar = sidecar.Contains("-wal" + RecoverySuffix, StringComparison.Ordinal)
                ? canonicalMainPath + "-wal"
                : canonicalMainPath + "-shm";
            if (File.Exists(sidecar))
            {
                _moveFile(sidecar, canonicalSidecar);
            }
        }
    }

    private void CleanupRestoreArtifacts(params IEnumerable<string>[] groups)
    {
        foreach (var path in groups.SelectMany(group => group).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
        }
    }

    private static IEnumerable<string> MainFiles(IEnumerable<string> paths) =>
        paths.Where(path =>
            !path.EndsWith("-wal", StringComparison.Ordinal)
            && !path.EndsWith("-shm", StringComparison.Ordinal)
            && !path.EndsWith(RecoverySuffix, StringComparison.Ordinal));

    private static IReadOnlyList<string> SafeFiles(
        string directory,
        string prefix,
        bool excludeOld = false,
        bool excludeFailed = false)
    {
        try
        {
            return Directory.GetFiles(directory)
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(path => !(excludeOld && path.Contains(".restore-old-", StringComparison.Ordinal)))
                .Where(path => !(excludeFailed && path.Contains(".restore-failed-", StringComparison.Ordinal)))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                // Rotation is best effort: a locked or unreadable file must not fail the backup
                // that was just created. The next rotation retries the leftover file. The
                // failure is now reported with the backup-prune phase instead of
                // staying silent.
                ReportFailure(PruneFailurePhase, exception);
            }
        }
    }

    private string NewBackupPath(bool isPreMigration) => Path.Combine(
        _options.BackupDirectory,
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{(isPreMigration ? PreMigrationMarker : string.Empty)}.db");

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

    private void CleanupCanonicalSidecars()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_options.DatabasePath + "-wal");
        TryDelete(_options.DatabasePath + "-shm");
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
            // Try the platform default once more when a test seam or transient wrapper fails.
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }

    private void ReportFailure(string phase, Exception exception)
    {
        try
        {
            FailureReporter?.Invoke(phase, exception);
        }
        catch
        {
            // Failure reporting must never change backup behavior.
        }
    }

    private async Task<CheckpointResult> CheckpointCurrentDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.DatabasePath))
        {
            return default;
        }

        try
        {
            await using var connection = new SqliteConnection(Database.ConnectionString(_options));
            await connection.OpenAsync(cancellationToken);
            Database.ConfigureConnection(connection);
            if (!await IsSqliteIntegrityOkAsync(connection, cancellationToken))
            {
                SqliteConnection.ClearAllPools();
                return new CheckpointResult(true);
            }
            var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
            if (!schemaVersion.HasValue)
            {
                SqliteConnection.ClearAllPools();
                return new CheckpointResult(true);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await connection.CloseAsync();
            SqliteConnection.ClearAllPools();
            return new CheckpointResult(false);
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode is SqliteErrorCorrupt
                or SqliteErrorNotADatabase
                )
        {
            SqliteConnection.ClearAllPools();
            // Corruption is precisely the condition a restore exists to fix: report it so the
            // caller keeps the unreadable file as a recovery set and installs the backup anyway.
            // Every other failure (locks, permissions, disk errors) must still abort the restore.
            return new CheckpointResult(true);
        }
    }

    private const int SqliteErrorCorrupt = 11;
    private const int SqliteErrorNotADatabase = 26;
    private readonly record struct CheckpointResult(bool Corrupt);

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
            if (!await HasSqliteHeaderAsync(backupPath, cancellationToken))
            {
                return false;
            }

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

    private static async Task<bool> HasSqliteHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken);
        return read == header.Length && header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
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
