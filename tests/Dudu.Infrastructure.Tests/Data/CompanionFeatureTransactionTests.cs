using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

public sealed class CompanionFeatureTransactionTests
{
    [Fact]
    public async Task Reminder_defaults_and_preferences_roll_back_together_after_last_write()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with
        {
            HydrationRemindersEnabled = true,
            BreakRemindersEnabled = false,
        };
        var service = new CompanionFeatureTransactionService(
            new AppUnitOfWork(fixture.Database),
            (point, _) => point == "after-default-reminders"
                ? Task.FromException(new IOException("injected default failure"))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() =>
            service.SavePreferencesAndDefaultRemindersAsync(
                preferences,
                DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
                TimeZoneInfo.Utc,
                TestContext.Current.CancellationToken));

        Assert.Null(await new PreferencesRepository(fixture.Database)
            .GetAsync(TestContext.Current.CancellationToken));
        var reminders = await new ReminderRepository(fixture.Database)
            .ListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(reminders, item =>
            item.Id is "default-hydration" or "default-break");
    }

    [Fact]
    public async Task Reminder_default_toggle_commits_stable_rows_with_current_policy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with
        {
            QuietHours = new QuietHours(true, new TimeOnly(21), new TimeOnly(7)),
            HydrationRemindersEnabled = true,
            BreakRemindersEnabled = false,
        };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferences, await new PreferencesRepository(fixture.Database)
            .GetAsync(TestContext.Current.CancellationToken));
        var defaults = (await new ReminderRepository(fixture.Database)
            .ListAsync(TestContext.Current.CancellationToken))
            .Where(item => item.Id is "default-hydration" or "default-break")
            .OrderBy(item => item.Id)
            .ToArray();
        Assert.Equal(2, defaults.Length);
        Assert.False(defaults.Single(item => item.Id == "default-break").Enabled);
        Assert.True(defaults.Single(item => item.Id == "default-hydration").Enabled);
        Assert.All(defaults, item =>
        {
            Assert.Equal("UTC", item.LocalTimeZoneId);
            Assert.Equal(preferences.QuietHours, item.QuietHours);
        });
    }

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

    private static Preferences TestPreferences() => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        ReducedMotion: false,
        LocalNoteDailyLimit: 3,
        LaunchAtSignIn: true,
        AlwaysOnTop: false,
        HidePetDuringFullscreen: true,
        AmbientMinimumInterval: TimeSpan.FromMinutes(15));

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
