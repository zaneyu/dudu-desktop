using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

/// <summary>
/// Removes every user-owned local record while preserving the database schema.
/// The bundled default notes are restored so the next launch starts like a
/// clean installation. Backups and machine-bound secrets are deleted too,
/// because otherwise "delete local data" would leave recoverable copies.
/// </summary>
public sealed class LocalDataMaintenanceService
{
    private readonly DatabaseOptions _options;
    private readonly Database _database;
    private readonly string _secretsDirectory;

    public LocalDataMaintenanceService(DatabaseOptions options, Database database)
        : this(
            options,
            database,
            Path.Combine(
                Path.GetDirectoryName(options.DatabasePath)
                    ?? throw new InvalidOperationException("The database path has no directory."),
                "secrets"))
    {
    }

    internal LocalDataMaintenanceService(
        DatabaseOptions options,
        Database database,
        string secretsDirectory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _secretsDirectory = Path.GetFullPath(
            secretsDirectory ?? throw new ArgumentNullException(nameof(secretsDirectory)));
    }

    public async Task DeleteAllUserDataAsync(CancellationToken cancellationToken = default)
    {
        // The maintenance lease is held for the entire wipe — rows, reseed, and file deletions.
        // Releasing it before deleting backups/secrets/restore artifacts would let a concurrent
        // writer recreate rows or files between the transactional delete and the file sweep,
        // leaving recoverable copies behind after "delete local data" reported success.
        using var maintenance = await _database.EnterMaintenanceAsync(cancellationToken);
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = _options.DatabasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    ForeignKeys = true,
                    // The WAL sidecars are removed below. Do not return this
                    // maintenance connection to the pool after its dispose;
                    // a pooled handle can keep the deleted WAL attached to
                    // the next connection and cause a disk I/O error.
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            Database.ConfigureConnection(connection);

            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM local_note_history;
                    DELETE FROM reminder_occurrences;
                    DELETE FROM focus_sessions;
                    DELETE FROM reminders;
                    DELETE FROM tasks;
                    DELETE FROM mood_check_ins;
                    DELETE FROM countdowns;
                    DELETE FROM remote_envelopes;
                    DELETE FROM processed_remote_messages;
                    DELETE FROM asset_packs;
                    DELETE FROM local_notes;
                    DELETE FROM pet_placements;
                    DELETE FROM preferences;
                    DELETE FROM profiles;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            await SeedData.SeedAsync(connection, cancellationToken);
        }

        DeleteFiles(_options.BackupDirectory, "*.db", cancellationToken);
        DeleteRestoreArtifacts(cancellationToken);
        DeleteFiles(_secretsDirectory, "*.bin", cancellationToken);
    }

    private static void DeleteFiles(
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
    }

    private void DeleteRestoreArtifacts(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (directory is null || !Directory.Exists(directory)) return;

        var databasePath = Path.GetFullPath(_options.DatabasePath);
        var prefixes = new[]
        {
            databasePath + ".restore-",
            databasePath + ".restore-old-",
            databasePath + ".restore-failed-",
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Equals(databasePath + "-wal", StringComparison.OrdinalIgnoreCase)
                || path.Equals(databasePath + "-shm", StringComparison.OrdinalIgnoreCase)
                || prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(path);
            }
        }
    }
}
