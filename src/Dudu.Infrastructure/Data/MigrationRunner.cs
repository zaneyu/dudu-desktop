using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public sealed record MigrationDefinition(int Version, string Sql);

public sealed class MigrationRunner
{
    private readonly DatabaseOptions _options;
    private readonly DatabaseBackupService _backups;
    private readonly List<MigrationDefinition> _migrations = [];

    public MigrationRunner(
        DatabaseOptions options,
        DatabaseBackupService? backups = null,
        IEnumerable<MigrationDefinition>? migrations = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backups = backups ?? new DatabaseBackupService(options);
        _migrations.AddRange(migrations ?? LoadEmbeddedMigrations());
    }

    public IReadOnlyList<MigrationDefinition> Migrations => _migrations;

    public void AddMigration(int version, string sql)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _migrations.RemoveAll(migration => migration.Version == version);
        _migrations.Add(new MigrationDefinition(version, sql));
        _migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
    }

    public async Task RunAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default,
        bool createBackups = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Database.ConfigureConnection(connection);

        var currentVersion = await ReadVersionAsync(connection, cancellationToken);
        // One backup per upgrade run, taken before the first migration that
        // actually applies. Backing up before every migration in the run let
        // a multi-step upgrade (more migrations pending than the retention
        // count) evict its own pre-upgrade backup -- the only copy of the
        // database as it existed before this run touched it -- before the
        // run even finished.
        var backedUpThisRun = false;
        foreach (var migration in _migrations.OrderBy(migration => migration.Version))
        {
            if (migration.Version <= currentVersion)
            {
                continue;
            }

            if (createBackups && !backedUpThisRun && File.Exists(_options.DatabasePath))
            {
                // A valid PRE-MIGRATION backup already sitting on disk at the exact version
                // we're about to upgrade from is the same pre-upgrade snapshot we'd take here
                // -- HasValidBackupAtSchemaVersionAsync only ever matches that kind, never a
                // manual or scheduled backup that merely happens to sit at this version.
                // Skipping the redundant copy makes the snapshot idempotent across repeated
                // attempts from the same starting version -- a fresh process retrying a
                // migration that keeps failing, for example -- instead of piling up redundant
                // backups that eventually evict the one genuine pre-upgrade copy.
                if (!await _backups.HasValidBackupAtSchemaVersionAsync(currentVersion, cancellationToken))
                {
                    await _backups.CreatePreMigrationBackupAsync(connection, cancellationToken);
                }

                backedUpThisRun = true;
            }

            // Some migrations (0009's DROP TABLE local_notes, for one) touch a
            // table that another table references with ON DELETE CASCADE. Per
            // SQLite's documented procedure for this kind of schema change
            // (https://www.sqlite.org/lang_altertable.html#otheralter), FK
            // enforcement must be disabled *before* the transaction starts --
            // PRAGMA foreign_keys is a documented no-op when set inside a
            // transaction, so doing it after BEGIN would not actually suppress
            // the cascade. We verify no dangling references were introduced with
            // foreign_key_check before committing, and restore enforcement
            // afterward regardless of outcome.
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=OFF;", cancellationToken);
            try
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = migration.Sql;
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var checkCommand = connection.CreateCommand())
                    {
                        checkCommand.Transaction = transaction;
                        checkCommand.CommandText = "PRAGMA foreign_key_check;";
                        await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            throw new SqliteException(
                                $"Migration {migration.Version} would leave dangling foreign key references.",
                                19);
                        }
                    }

                    await using (var versionCommand = connection.CreateCommand())
                    {
                        versionCommand.Transaction = transaction;
                        versionCommand.CommandText = """
                            INSERT INTO schema_version (id, version)
                            VALUES (1, $version)
                            ON CONFLICT(id) DO UPDATE SET version = excluded.version;
                            """;
                        versionCommand.Parameters.AddWithValue("$version", migration.Version);
                        await versionCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                    currentVersion = migration.Version;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            finally
            {
                await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", CancellationToken.None);
            }
        }
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task RunMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        RunAsync(connection, cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
            ?? throw new InvalidOperationException("The database path has no directory."));
        await using var connection = new SqliteConnection(Database.ConnectionString(_options));
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);
        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL;";
            await journalMode.ExecuteNonQueryAsync(cancellationToken);
        }
        await RunAsync(connection, cancellationToken);
    }

    public Task RunMigrationsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken);

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_version';
            """;
        var tableExists = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
        if (!tableExists)
        {
            return 0;
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
    }

    private static IReadOnlyList<MigrationDefinition> LoadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Data.Migrations.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                var migrationName = name[(name.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
                var versionText = migrationName[..migrationName.IndexOf('_')];
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded migration '{name}' was not found.");
                using var reader = new StreamReader(stream);
                return new MigrationDefinition(int.Parse(versionText), reader.ReadToEnd());
            })
            .OrderBy(migration => migration.Version)
            .ToArray();
    }
}
