using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>The love notes page's "opened notes" list: only revealed partner notes
/// (<c>remote-&lt;messageId&gt;</c> rows), newest first. Seeded defaults and older
/// hand-written jar notes stay in the table but are never listed.</summary>
public sealed class LocalNoteRepositoryRemoteListTests
{
    [Fact]
    public async Task ListRemoteAsync_returns_only_remote_prefixed_notes_most_recent_insert_first()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repo = new LocalNoteRepository(fixture.Database);
        var ct = TestContext.Current.CancellationToken;
        await repo.SaveToJarAsync(new LocalLoveNote("remote-1", "first"), ct);
        await repo.SaveToJarAsync(new LocalLoveNote("seed-1", "seeded"), ct);
        await repo.SaveToJarAsync(new LocalLoveNote("remote-2", "second"), ct);

        var remote = await repo.ListRemoteAsync(ct);

        Assert.Equal(["remote-2", "remote-1"], remote.Select(n => n.Id).ToArray());
        Assert.Equal(["second", "first"], remote.Select(n => n.Text).ToArray());
    }

    [Fact]
    public async Task ListRemoteAsync_ignores_ids_that_only_look_like_the_prefix()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repo = new LocalNoteRepository(fixture.Database);
        var ct = TestContext.Current.CancellationToken;
        await repo.SaveToJarAsync(new LocalLoveNote("remote_1", "underscore"), ct);
        await repo.SaveToJarAsync(new LocalLoveNote("my-remote-1", "infix"), ct);
        await repo.SaveToJarAsync(new LocalLoveNote("remote-1", "real"), ct);

        var remote = await repo.ListRemoteAsync(ct);

        Assert.Equal("remote-1", Assert.Single(remote).Id);
    }

    [Fact]
    public async Task Resaving_a_remote_note_keeps_its_place_and_delete_removes_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repo = new LocalNoteRepository(fixture.Database);
        var ct = TestContext.Current.CancellationToken;
        await repo.SaveToJarAsync(new LocalLoveNote("remote-1", "first"), ct);
        await repo.SaveToJarAsync(new LocalLoveNote("remote-2", "second"), ct);

        // ON CONFLICT DO UPDATE keeps the row's rowid, so the upsert does not jump it
        // to the top of the list.
        await repo.SaveToJarAsync(new LocalLoveNote("remote-1", "first again"), ct);
        Assert.Equal(["remote-2", "remote-1"], (await repo.ListRemoteAsync(ct)).Select(n => n.Id).ToArray());
        Assert.Equal("first again", (await repo.ListRemoteAsync(ct))[1].Text);

        await repo.DeleteAsync("remote-2", ct);

        Assert.Equal("remote-1", Assert.Single(await repo.ListRemoteAsync(ct)).Id);
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string root, Database database)
        {
            Root = root;
            Database = database;
        }

        private string Root { get; }
        public Database Database { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            return new DatabaseFixture(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
