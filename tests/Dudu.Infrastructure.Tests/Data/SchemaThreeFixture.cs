using Dudu.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// Builds a database matching the user's actually-installed build (tag
/// <c>v1.0.0-private.1</c>, schema version 3 -- migrations 0001-0003 only),
/// seeded the way that build's SeedData did, then used the way a real
/// install would be: some default notes deleted, a user-written note added,
/// a reminder and a task present. Tests exercising the upgrade path (0004
/// onward) start here instead of from a fresh database, since every other
/// fixture in this project starts fresh and the upgrade path itself was
/// otherwise untested.
///
/// Migrations 0001-0003 are verified byte-identical between HEAD and the
/// tag (`git show v1.0.0-private.1:&lt;path&gt;`), so reading them from the
/// current <see cref="MigrationRunner"/> (embedded resources) and running
/// only those reproduces exactly what that build's MigrationRunner applied,
/// via the same version-bookkeeping (schema_version upsert) it used --
/// verified unchanged at the tag except for the later, purely additive
/// createBackups parameter.
/// </summary>
internal sealed class SchemaThreeFixture : IAsyncDisposable
{
    /// <summary>Exactly the twelve default notes SeedData.SeedAsync inserted at
    /// v1.0.0-private.1 (tag SeedData.cs), before the wording was later revised.</summary>
    public static readonly (string Id, string Text)[] ShippedDefaultNotes =
    [
        ("default-note-01", "u can de"),
        ("default-note-02", "rest rest abit ok"),
        ("default-note-03", "good job today lovuh"),
        ("default-note-04", "namnam properly ah"),
        ("default-note-05", "soon soon"),
        ("default-note-06", "drink water ah"),
        ("default-note-07", "proud of u"),
        ("default-note-08", "sleep early tonight ok"),
        ("default-note-09", "mwamwa"),
        ("default-note-10", "try try today lovuh"),
        ("default-note-11", "isok rest abit"),
        ("default-note-12", "busy is good today"),
    ];

    /// <summary>Default notes this "used install" deleted, simulating a person who
    /// cleared out a couple of default notes she didn't want.</summary>
    public static readonly string[] DeletedDefaultNoteIds = ["default-note-05", "default-note-09"];

    public const string UserNoteId = "user-note-1";
    public const string UserNoteText = "i love u";
    public const string ReminderId = "reminder-1";
    public const string TaskId = "task-1";

    private SchemaThreeFixture(string root, DatabaseOptions options)
    {
        Root = root;
        Options = options;
    }

    public string Root { get; }
    public DatabaseOptions Options { get; }

    public static async Task<SchemaThreeFixture> CreateAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-schema3-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));

        await using var connection = new SqliteConnection(Database.ConnectionString(options));
        await connection.OpenAsync(cancellationToken);
        Database.ConfigureConnection(connection);

        var schemaThreeMigrations = new MigrationRunner(options).Migrations
            .Where(migration => migration.Version <= 3)
            .ToArray();
        await new MigrationRunner(options, migrations: schemaThreeMigrations)
            .RunAsync(connection, cancellationToken, createBackups: false);

        await SeedShippedDefaultsAsync(connection, cancellationToken);
        await SimulateUsedInstallAsync(connection, cancellationToken);

        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);

        return new SchemaThreeFixture(root, options);
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private static async Task SeedShippedDefaultsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Schema version 3 predates seed_state (0007_seed_watermark.sql), so this
        // mirrors that build's SeedData.SeedAsync directly: unconditional insert,
        // no watermark.
        foreach (var (id, text) in ShippedDefaultNotes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_notes (id, text, enabled, is_default)
                VALUES ($id, $text, 1, 1)
                ON CONFLICT(id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$text", text);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SimulateUsedInstallAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var deletedId in DeletedDefaultNoteIds)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM local_notes WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", deletedId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var userNote = connection.CreateCommand())
        {
            userNote.CommandText = """
                INSERT INTO local_notes (id, text, enabled, is_default)
                VALUES ($id, $text, 1, 0);
                """;
            userNote.Parameters.AddWithValue("$id", UserNoteId);
            userNote.Parameters.AddWithValue("$text", UserNoteText);
            await userNote.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var reminder = connection.CreateCommand())
        {
            reminder.CommandText = """
                INSERT INTO reminders
                    (id, title, details, enabled, rule_kind, local_time, weekdays_mask,
                     interval_ticks, first_due_utc, local_time_zone_id, quiet_hours_behavior,
                     missed_policy, next_due_utc, deliver_after_utc, snoozed_until_utc,
                     quiet_hours_enabled, quiet_hours_start, quiet_hours_end)
                VALUES
                    ($id, 'Drink water', NULL, 1, 0, NULL, NULL,
                     NULL, '2026-01-01T09:00:00Z', 'UTC', 0,
                     0, '2026-01-01T09:00:00Z', NULL, NULL,
                     NULL, NULL, NULL);
                """;
            reminder.Parameters.AddWithValue("$id", ReminderId);
            await reminder.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var task = connection.CreateCommand())
        {
            task.CommandText = """
                INSERT INTO tasks (id, title, notes, due_utc, is_completed, created_utc, updated_utc, completed_utc)
                VALUES ($id, 'Fold laundry', NULL, NULL, 0, '2026-01-01T08:00:00Z', '2026-01-01T08:00:00Z', NULL);
                """;
            task.Parameters.AddWithValue("$id", TaskId);
            await task.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
