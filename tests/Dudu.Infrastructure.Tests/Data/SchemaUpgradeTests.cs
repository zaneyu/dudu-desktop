using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// Exercises the upgrade path from the user's actually-installed schema
/// (v1.0.0-private.1, schema version 3) forward, via <see cref="SchemaThreeFixture"/>.
/// Every other Infrastructure data test starts from a fresh database, which
/// never touches this path.
/// </summary>
public sealed class SchemaUpgradeTests
{
    [Fact]
    public async Task Schema_three_fixture_reproduces_a_used_install_before_upgrading()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SchemaThreeFixture.CreateAsync(cancellationToken);

        await using var connection = new SqliteConnection(Database.ConnectionString(fixture.Options));
        await connection.OpenAsync(cancellationToken);

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_version WHERE id = 1;";
        Assert.Equal(3L, Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken)));

        await using var noteCount = connection.CreateCommand();
        noteCount.CommandText = "SELECT COUNT(*) FROM local_notes;";
        // Twelve shipped defaults minus two deleted, plus one user-written note.
        Assert.Equal(11L, Convert.ToInt64(await noteCount.ExecuteScalarAsync(cancellationToken)));

        await using var reminderCount = connection.CreateCommand();
        reminderCount.CommandText = "SELECT COUNT(*) FROM reminders;";
        Assert.Equal(1L, Convert.ToInt64(await reminderCount.ExecuteScalarAsync(cancellationToken)));

        await using var taskCount = connection.CreateCommand();
        taskCount.CommandText = "SELECT COUNT(*) FROM tasks;";
        Assert.Equal(1L, Convert.ToInt64(await taskCount.ExecuteScalarAsync(cancellationToken)));

        await using var seedStateExists = connection.CreateCommand();
        seedStateExists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'seed_state';";
        Assert.Equal(0L, Convert.ToInt64(await seedStateExists.ExecuteScalarAsync(cancellationToken)));
    }

    [Fact]
    public async Task Saving_two_notes_with_identical_text_both_persist_on_a_fresh_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            await using var database = await Database.OpenAsync(options, cancellationToken);
            var repository = new LocalNoteRepository(database);
            var first = new LocalLoveNote("note-a", "i love u", Enabled: true);
            var second = new LocalLoveNote("note-b", "i love u", Enabled: true);

            await repository.SaveToJarAsync(first, cancellationToken);
            await repository.SaveToJarAsync(second, cancellationToken);

            var notes = await repository.ListAsync(cancellationToken);
            Assert.Contains(first, notes);
            Assert.Contains(second, notes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Saving_two_notes_with_identical_text_both_persist_after_upgrading_from_schema_three()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SchemaThreeFixture.CreateAsync(cancellationToken);
        await using var database = await Database.OpenAsync(fixture.Options, cancellationToken);
        var repository = new LocalNoteRepository(database);
        var first = new LocalLoveNote("note-a", "i love u too", Enabled: true);
        var second = new LocalLoveNote("note-b", "i love u too", Enabled: true);

        await repository.SaveToJarAsync(first, cancellationToken);
        await repository.SaveToJarAsync(second, cancellationToken);

        var notes = await repository.ListAsync(cancellationToken);
        Assert.Contains(first, notes);
        Assert.Contains(second, notes);
    }

    [Fact]
    public async Task Upgrading_from_schema_three_keeps_deleted_defaults_deleted_and_completes_db_init()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SchemaThreeFixture.CreateAsync(cancellationToken);

        // Database.OpenAsync running to completion here *is* the db-init success
        // assertion: SeedData.SeedAsync runs as part of it, and the old
        // UNIQUE(text) + missing watermark combination (B7 + seed resurrection)
        // could make it throw on this exact upgrade path.
        await using var database = await Database.OpenAsync(fixture.Options, cancellationToken);

        var notes = await new LocalNoteRepository(database).ListAsync(cancellationToken);
        foreach (var deletedId in SchemaThreeFixture.DeletedDefaultNoteIds)
        {
            Assert.DoesNotContain(notes, note => note.Id == deletedId);
        }

        Assert.Equal(
            SchemaThreeFixture.ShippedDefaultNotes.Length - SchemaThreeFixture.DeletedDefaultNoteIds.Length + 1,
            notes.Count);

        await using var connection = await database.CreateConnectionAsync(cancellationToken);
        await using var reminderCount = connection.CreateCommand();
        reminderCount.CommandText = "SELECT COUNT(*) FROM reminders WHERE id = $id;";
        reminderCount.Parameters.AddWithValue("$id", SchemaThreeFixture.ReminderId);
        Assert.Equal(1L, Convert.ToInt64(await reminderCount.ExecuteScalarAsync(cancellationToken)));

        await using var taskCount = connection.CreateCommand();
        taskCount.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = $id;";
        taskCount.Parameters.AddWithValue("$id", SchemaThreeFixture.TaskId);
        Assert.Equal(1L, Convert.ToInt64(await taskCount.ExecuteScalarAsync(cancellationToken)));

        // An explicit re-seed (as a later launch's db-init would trigger) stays a no-op.
        await using var reseedConnection = await database.CreateConnectionAsync(cancellationToken);
        await SeedData.SeedAsync(reseedConnection, cancellationToken);
        var afterReseed = await new LocalNoteRepository(database).ListAsync(cancellationToken);
        foreach (var deletedId in SchemaThreeFixture.DeletedDefaultNoteIds)
        {
            Assert.DoesNotContain(afterReseed, note => note.Id == deletedId);
        }
        Assert.Equal(notes.Count, afterReseed.Count);
    }

    [Fact]
    public async Task Upgrading_from_schema_three_creates_exactly_one_pre_upgrade_backup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SchemaThreeFixture.CreateAsync(cancellationToken);

        // Six pending migrations (4-10 minus whichever are no-ops) apply in this
        // single upgrade run -- more than the default backup retention count of 5.
        // The old per-migration backup would create one snapshot per migration and
        // evict the earliest (the only genuine pre-upgrade snapshot) before the run
        // even finished.
        await using var database = await Database.OpenAsync(fixture.Options, cancellationToken);

        var backups = Directory.GetFiles(fixture.Options.BackupDirectory, "*.db");
        Assert.Single(backups);

        await using var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backups[0],
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await backupConnection.OpenAsync(cancellationToken);
        await using var version = backupConnection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_version WHERE id = 1;";
        Assert.Equal(3L, Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken)));
    }
}
