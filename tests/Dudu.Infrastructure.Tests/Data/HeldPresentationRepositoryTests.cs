using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// P2-B: <see cref="HeldPresentationRepository"/> backs PresentationPolicy's
/// in-memory held queue so a suppressed reminder/remote-note/local-note
/// survives an app quit or crash instead of vanishing with that in-memory
/// state. These tests cover the fresh-database roundtrip and the upgrade
/// path from the user's actually-installed schema (v1.0.0-private.1, schema
/// version 3), matching the pattern in <see cref="SchemaUpgradeTests"/>.
/// </summary>
public sealed class HeldPresentationRepositoryTests
{
    [Fact]
    public async Task Save_then_list_round_trips_every_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            await using var database = await Database.OpenAsync(options, cancellationToken);
            var repository = new HeldPresentationRepository(database);
            var queuedUtc = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
            var expiresUtc = DateTimeOffset.Parse("2026-09-19T00:00:00Z");
            var item = new HeldPresentation(
                "Reminder:bedtime",
                "Reminder",
                "bedtime",
                "Goodnight",
                "time for bed",
                "sleep",
                expiresUtc,
                queuedUtc,
                Toasted: true);

            await repository.SaveAsync(item, cancellationToken);
            var loaded = Assert.Single(await repository.ListAsync(cancellationToken));

            Assert.Equal(item, loaded);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Save_upserts_an_existing_key_instead_of_duplicating_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            await using var database = await Database.OpenAsync(options, cancellationToken);
            var repository = new HeldPresentationRepository(database);
            var queuedUtc = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
            await repository.SaveAsync(
                new HeldPresentation("Reminder:bedtime", "Reminder", "bedtime", "Goodnight", null, null, null, queuedUtc, Toasted: false),
                cancellationToken);

            // A failed presentation attempt requeues the same key -- this must
            // replace the row (e.g. a later queuedUtc), never duplicate it.
            var requeuedUtc = queuedUtc.AddMinutes(5);
            await repository.SaveAsync(
                new HeldPresentation("Reminder:bedtime", "Reminder", "bedtime", "Goodnight", "retry", "sleep", null, requeuedUtc, Toasted: true),
                cancellationToken);

            var loaded = Assert.Single(await repository.ListAsync(cancellationToken));
            Assert.Equal("retry", loaded.Body);
            Assert.Equal("sleep", loaded.AnimationKey);
            Assert.Equal(requeuedUtc, loaded.QueuedUtc);
            Assert.True(loaded.Toasted);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Delete_removes_the_row_and_is_a_no_op_for_an_unknown_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            await using var database = await Database.OpenAsync(options, cancellationToken);
            var repository = new HeldPresentationRepository(database);
            var item = new HeldPresentation(
                "RemoteNote:11111111-1111-1111-1111-111111111111",
                "RemoteNote",
                "11111111-1111-1111-1111-111111111111",
                null,
                null,
                null,
                null,
                DateTimeOffset.Parse("2026-09-18T20:00:00Z"),
                Toasted: false);
            await repository.SaveAsync(item, cancellationToken);

            await repository.DeleteAsync("does-not-exist", cancellationToken);
            Assert.Single(await repository.ListAsync(cancellationToken));

            await repository.DeleteAsync(item.Key, cancellationToken);
            Assert.Empty(await repository.ListAsync(cancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ListAsync_orders_by_queued_utc_so_older_held_items_load_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            await using var database = await Database.OpenAsync(options, cancellationToken);
            var repository = new HeldPresentationRepository(database);
            var older = DateTimeOffset.Parse("2026-09-18T18:00:00Z");
            var newer = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
            await repository.SaveAsync(
                new HeldPresentation("Reminder:second", "Reminder", "second", "Hydrate", null, null, null, newer, Toasted: false),
                cancellationToken);
            await repository.SaveAsync(
                new HeldPresentation("Reminder:first", "Reminder", "first", "Stretch", null, null, null, older, Toasted: false),
                cancellationToken);

            var loaded = await repository.ListAsync(cancellationToken);

            Assert.Equal(["first", "second"], loaded.Select(record => record.Id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Held_presentations_table_exists_after_upgrading_from_schema_three()
    {
        // Regression coverage for the 0011 migration: a real install still on
        // schema version 3 must gain the held_presentations table on its next
        // launch instead of PresentationCoordinator failing to resolve
        // IHeldPresentationRepository at startup.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SchemaThreeFixture.CreateAsync(cancellationToken);
        await using var database = await Database.OpenAsync(fixture.Options, cancellationToken);
        var repository = new HeldPresentationRepository(database);

        Assert.Empty(await repository.ListAsync(cancellationToken));

        var item = new HeldPresentation(
            "Reminder:bedtime",
            "Reminder",
            "bedtime",
            "Goodnight",
            null,
            "sleep",
            null,
            DateTimeOffset.Parse("2026-09-18T20:00:00Z"),
            Toasted: false);
        await repository.SaveAsync(item, cancellationToken);

        Assert.Equal(item, Assert.Single(await repository.ListAsync(cancellationToken)));
    }
}
