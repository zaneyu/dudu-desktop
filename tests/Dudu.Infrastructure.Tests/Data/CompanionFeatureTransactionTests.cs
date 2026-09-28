using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

public sealed class CompanionFeatureTransactionTests
{
    [Fact]
    public async Task Remote_note_and_consumed_envelope_roll_back_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelopes = new RemoteEnvelopeRepository(fixture.Database);
        var envelope = new RemoteEnvelope(
            "transaction-note",
            [1],
            null,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"));
        Assert.True(await envelopes.TryInsertAsync(envelope, TestContext.Current.CancellationToken));
        var service = new CompanionFeatureTransactionService(
            new AppUnitOfWork(fixture.Database),
            (point, _) => point == "after-envelope-consume"
                ? Task.FromException(new IOException("injected consume failure"))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveRemoteNoteAndConsumeEnvelopeAsync(
                new LocalLoveNote("remote-transaction-note", "Still here"),
                envelope.MessageId,
                DateTimeOffset.Parse("2026-09-12T10:01:00Z"),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            await new LocalNoteRepository(fixture.Database)
                .ListAsync(TestContext.Current.CancellationToken),
            note => note.Id == "remote-transaction-note");
        Assert.NotNull(await envelopes.GetAsync(envelope.MessageId, TestContext.Current.CancellationToken));
        Assert.False(await envelopes.IsProcessedAsync(envelope.MessageId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remote_note_save_and_envelope_consume_commit_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelopes = new RemoteEnvelopeRepository(fixture.Database);
        var envelope = new RemoteEnvelope(
            "transaction-note-ok",
            [1],
            null,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"));
        Assert.True(await envelopes.TryInsertAsync(envelope, TestContext.Current.CancellationToken));
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));

        await service.SaveRemoteNoteAndConsumeEnvelopeAsync(
            new LocalLoveNote("remote-transaction-note-ok", "kept"),
            envelope.MessageId,
            DateTimeOffset.Parse("2026-09-12T10:01:00Z"),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            await new LocalNoteRepository(fixture.Database)
                .ListAsync(TestContext.Current.CancellationToken),
            note => note.Id == "remote-transaction-note-ok");
        Assert.True(await envelopes.IsProcessedAsync(envelope.MessageId, TestContext.Current.CancellationToken));
    }

    private sealed class Fixture(string root, Database database) : IAsyncDisposable
    {
        public Database Database { get; } = database;

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-feature-transactions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var database = await Database.OpenAsync(new DatabaseOptions(Path.Combine(root, "dudu.db")));
            return new Fixture(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
