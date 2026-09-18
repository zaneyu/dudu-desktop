using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

public sealed class SeedDataTests
{
    [Fact]
    public async Task Seeding_a_fresh_database_plants_exactly_twelve_distinct_default_notes()
    {
        await using var fixture = await SeedFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var notes = await fixture.Repository.ListAsync(cancellationToken);

        Assert.Equal(12, notes.Count);
        Assert.Equal(12, notes.Select(note => note.Text).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, 12).Select(number => $"default-note-{number:00}"),
            notes.Select(note => note.Id));
        Assert.All(notes, note => Assert.True(note.Enabled));
        Assert.Equal("jiayo ada one small step", notes[0].Text);
        Assert.Equal("so lihai de ada good job today", notes[2].Text);
        Assert.Equal("namnam break time", notes[3].Text);
        Assert.Equal("time to shuijiaojiao", notes[7].Text);
        Assert.Equal("a little mwamwa from dudu", notes[8].Text);
        Assert.Equal("dudu peipei while u try", notes[9].Text);

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_notes WHERE is_default = 1;";
        Assert.Equal(12L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
    }

    [Fact]
    public async Task Reseeding_twice_keeps_twelve_notes_and_preserves_user_edits()
    {
        await using var fixture = await SeedFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var edited = new LocalLoveNote("default-note-06", "user edited this one", Enabled: false);
        await fixture.Repository.SaveToJarAsync(edited, cancellationToken);
        var before = await fixture.Repository.ListAsync(cancellationToken);

        await SeedData.SeedAsync(fixture.Connection, cancellationToken);
        await SeedData.SeedAsync(fixture.Connection, cancellationToken);

        var notes = await fixture.Repository.ListAsync(cancellationToken);

        Assert.Equal(12, notes.Count);
        Assert.Equal(before, notes);
        Assert.Contains(edited, notes);
        Assert.DoesNotContain(edited, await fixture.Repository.ListEnabledAsync(cancellationToken));
    }

    [Fact]
    public async Task Deleted_default_stays_deleted_across_reinitialization_and_explicit_reseed()
    {
        await using var fixture = await SeedFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        await fixture.Repository.DeleteAsync("default-note-09", cancellationToken);

        // Simulate the next process launch: drop the cached initialization so
        // InitializeAsync really re-runs its seed step.
        SqliteConnection.ClearAllPools();
        fixture.Database.InvalidateInitialization();
        await fixture.Database.InitializeAsync(cancellationToken);

        var remaining = await fixture.Repository.ListAsync(cancellationToken);
        Assert.DoesNotContain(remaining, note => note.Id == "default-note-09");
        Assert.Equal(11, remaining.Count);

        // An explicit reseed is a no-op once the watermark exists, too.
        await SeedData.SeedAsync(fixture.Connection, cancellationToken);

        remaining = await fixture.Repository.ListAsync(cancellationToken);
        Assert.DoesNotContain(remaining, note => note.Id == "default-note-09");
        Assert.Equal(11, remaining.Count);
    }

    private sealed class SeedFixture : IAsyncDisposable
    {
        private SeedFixture(string root, Database database, SqliteConnection connection, LocalNoteRepository repository)
        {
            Root = root;
            Database = database;
            Connection = connection;
            Repository = repository;
        }

        private string Root { get; }
        public Database Database { get; }
        public SqliteConnection Connection { get; }
        public LocalNoteRepository Repository { get; }

        public static async Task<SeedFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-seed-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var database = await Database.OpenAsync(
                new DatabaseOptions(Path.Combine(root, "dudu.db")),
                TestContext.Current.CancellationToken);
            var connection = await database.CreateConnectionAsync(TestContext.Current.CancellationToken);
            return new SeedFixture(root, database, connection, new LocalNoteRepository(database));
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Database.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
