using Dudu.Core.Models;
using Dudu.Core.Policies;
using Dudu.Core.Reminders;
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
            item.Id is "default-hydration" or "default-break"
                or LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId);
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
        Assert.All(defaults.Where(item => item.Id is "default-hydration" or "default-break"), item =>
        {
            Assert.Equal("UTC", item.LocalTimeZoneId);
            Assert.Equal(preferences.QuietHours, item.QuietHours);
        });
    }

    [Fact]
    public async Task Routine_defaults_commit_without_quiet_hour_shift_and_roll_back_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with
        {
            QuietHours = new QuietHours(true, new TimeOnly(21), new TimeOnly(7)),
            EveningCheckInEnabled = true,
            BedtimeRitualEnabled = true,
        };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferences, await new PreferencesRepository(fixture.Database)
            .GetAsync(TestContext.Current.CancellationToken));
        var evening = (await new ReminderRepository(fixture.Database)
            .ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.EveningCheckInId);
        var bedtime = (await new ReminderRepository(fixture.Database)
            .ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero), evening.NextDueUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 22, 0, 0, TimeSpan.Zero), bedtime.NextDueUtc);
        Assert.All(new[] { evening, bedtime }, item =>
        {
            Assert.Null(item.QuietHours);
            Assert.Equal(QuietHoursBehavior.WaitUntilQuietHoursEnd, item.QuietHoursBehavior);
            Assert.Equal(MissedOccurrencePolicy.Skip, item.MissedPolicy);
            Assert.Equal("UTC", item.LocalTimeZoneId);
        });

        var restoring = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        await restoring.RestorePreferencesAndDefaultRemindersAsync(
            TestPreferences() with
            {
                QuietHours = new QuietHours(true, new TimeOnly(21), new TimeOnly(7)),
            },
            [
                new Reminder(
                    LocalReminderDefaults.EveningCheckInId,
                    "how was your day, ada?",
                    null,
                    false,
                    new RecurrenceRule.Daily(new TimeOnly(20, 0)),
                    "UTC",
                    QuietHoursBehavior.WaitUntilQuietHoursEnd,
                    MissedOccurrencePolicy.Skip,
                    DateTimeOffset.Parse("2026-09-13T20:00:00Z")),
            ],
            TestContext.Current.CancellationToken);
        var restored = (await new ReminderRepository(fixture.Database)
            .ListAsync(TestContext.Current.CancellationToken))
            .Where(item => item.Id is LocalReminderDefaults.EveningCheckInId
                or LocalReminderDefaults.BedtimeId)
            .ToArray();
        Assert.Single(restored);
        Assert.False(restored[0].Enabled);
    }

    [Fact]
    public async Task Saving_preferences_again_preserves_next_due_and_snooze_for_an_unchanged_default()
    {
        // Opus review follow-up on finding 3: LocalReminderDefaults.Create
        // rebuilds every default from scratch on every save, which used to wipe
        // NextDueUtc/SnoozedUntilUtc even for a default whose own schedule this
        // save never touched. The fix now lives here (not in the ViewModel) so
        // it commits atomically with the rest of the transaction.
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { HydrationRemindersEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        // Simulate an in-flight snooze on the hydration default that a plain
        // rebuild would otherwise clobber.
        var beforeSecondSave = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        var snoozed = beforeSecondSave with
        {
            NextDueUtc = DateTimeOffset.Parse("2026-09-12T11:45:00Z"),
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        await reminderRepository.SaveAsync(snoozed, TestContext.Current.CancellationToken);

        // Enabled, Rule and LocalTimeZoneId are all unchanged from what
        // LocalReminderDefaults.Create would produce for "default-hydration".
        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        Assert.Equal(snoozed.NextDueUtc, saved.NextDueUtc);
        Assert.Equal(snoozed.SnoozedUntilUtc, saved.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Saving_preferences_again_preserves_a_user_renamed_default_title_and_details()
    {
        // Opus review finding 8: the Reminders page edits a default
        // reminder's Title/Details two-way. LocalReminderDefaults.Create
        // rebuilds every default from scratch on every preferences save,
        // which used to silently discard that rename on the very next save
        // -- even when nothing about the default's own schedule changed.
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { BedtimeRitualEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var renamed = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId) with
        {
            Title = "go to sleep already",
            Details = "phone down, lights off.",
        };
        await reminderRepository.SaveAsync(renamed, TestContext.Current.CancellationToken);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId);
        Assert.Equal("go to sleep already", saved.Title);
        Assert.Equal("phone down, lights off.", saved.Details);
    }

    [Fact]
    public async Task Saving_preferences_still_migrates_a_legacy_default_title_to_the_neutral_text()
    {
        // A row saved by an older install has the pre-personalisation "ada"
        // text baked in verbatim. That is a recognized default text (not a
        // user rename), so it must keep upgrading to the neutral title --
        // the preserve fix above must not freeze legacy rows in place.
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { BedtimeRitualEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var legacy = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId) with
        {
            Title = LocalReminderDefaults.BedtimeLegacyTitle,
            Details = LocalReminderDefaults.BedtimeLegacyDetails,
        };
        await reminderRepository.SaveAsync(legacy, TestContext.Current.CancellationToken);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId);
        Assert.Equal(LocalReminderDefaults.BedtimeDefaultTitle, saved.Title);
        Assert.Equal(LocalReminderDefaults.BedtimeDefaultDetails, saved.Details);
    }

    [Fact]
    public async Task Saving_preferences_preserves_a_cleared_default_details_field()
    {
        // Opus review follow-up D: the preserve check required a non-null
        // previous Details (`previous.Details is { } previousDetails`), so
        // clearing Details to null on the Reminders page was invisible to
        // it -- the very next preferences save silently regenerated the
        // shipped default text instead of keeping the clear.
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { BedtimeRitualEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var cleared = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId) with
        {
            Details = null,
        };
        await reminderRepository.SaveAsync(cleared, TestContext.Current.CancellationToken);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == LocalReminderDefaults.BedtimeId);
        Assert.Null(saved.Details);
    }

    [Fact]
    public async Task Saving_preferences_preserves_a_user_edited_rule_and_its_pending_schedule()
    {
        // Audit finding 2: Rule/QuietHoursBehavior/MissedPolicy are a hardcoded
        // per-id default in LocalReminderDefaults.Create, never derived from
        // Preferences -- so a schedule edited on the Reminders page (and any
        // snooze pending against it) must survive an unrelated preferences
        // save. This used to snap a user-edited "drink water" time back to
        // the shipped default and discard its snooze.
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { HydrationRemindersEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        // Simulate the Reminders page moving the default's time to 11:00 and
        // relaxing its quiet-hours/missed-occurrence behavior, with a snooze
        // pending against that edited schedule.
        var edited = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration") with
        {
            Rule = new RecurrenceRule.Daily(new TimeOnly(11, 0)),
            QuietHoursBehavior = QuietHoursBehavior.DeliverImmediately,
            MissedPolicy = MissedOccurrencePolicy.Skip,
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        await reminderRepository.SaveAsync(edited, TestContext.Current.CancellationToken);

        // An unrelated preferences save (same Enabled flag, same time zone)
        // must not touch this default's schedule.
        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        Assert.Equal(new RecurrenceRule.Daily(new TimeOnly(11, 0)), saved.Rule);
        Assert.Equal(QuietHoursBehavior.DeliverImmediately, saved.QuietHoursBehavior);
        Assert.Equal(MissedOccurrencePolicy.Skip, saved.MissedPolicy);
        Assert.Equal(edited.NextDueUtc, saved.NextDueUtc);
        Assert.Equal(edited.SnoozedUntilUtc, saved.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Saving_preferences_with_a_toggled_default_discards_the_stale_snooze()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            TestPreferences() with { HydrationRemindersEnabled = true },
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var snoozed = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration") with
        {
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        await reminderRepository.SaveAsync(snoozed, TestContext.Current.CancellationToken);

        // Toggling the default off changes Enabled, so it must not be treated
        // as "unchanged" even though the rule and time zone are identical.
        await service.SavePreferencesAndDefaultRemindersAsync(
            TestPreferences() with { HydrationRemindersEnabled = false },
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        Assert.False(saved.Enabled);
        Assert.Null(saved.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Saving_preferences_with_a_changed_time_zone_discards_the_stale_snooze()
    {
        // A Singapore-to-London move must not carry a Singapore-local due time
        // forward under the London zone (e.g. firing "drink water" at 03:00).
        await using var fixture = await Fixture.CreateAsync();
        var preferences = TestPreferences() with { HydrationRemindersEnabled = true };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);
        var singapore = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            singapore,
            TestContext.Current.CancellationToken);

        var snoozed = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration") with
        {
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        await reminderRepository.SaveAsync(snoozed, TestContext.Current.CancellationToken);

        await service.SavePreferencesAndDefaultRemindersAsync(
            preferences,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            london,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        Assert.Equal("Europe/London", saved.LocalTimeZoneId);
        Assert.Null(saved.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Saving_preferences_recomputes_next_due_from_the_preserved_rule_when_re_enabling()
    {
        // Opus review follow-up B: Rule/QuietHoursBehavior/MissedPolicy are
        // preserved from the existing row, but NextDueUtc still came from
        // LocalReminderDefaults.Create's own SHIPPED Daily(10:00) rule
        // whenever Enabled changed -- inconsistent with an edited rule
        // restored a few lines above it. Re-enabling an Interval(15m)-edited
        // default must resume on that interval, not jump back to the
        // shipped daily 10:00.
        await using var fixture = await Fixture.CreateAsync();
        var enabled = TestPreferences() with { HydrationRemindersEnabled = true };
        var disabled = TestPreferences() with { HydrationRemindersEnabled = false };
        var service = new CompanionFeatureTransactionService(new AppUnitOfWork(fixture.Database));
        var reminderRepository = new ReminderRepository(fixture.Database);

        await service.SavePreferencesAndDefaultRemindersAsync(
            enabled,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        // Edit the schedule to a 15-minute interval, as the Reminders page would.
        var edited = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration") with
        {
            Rule = new RecurrenceRule.Interval(TimeSpan.FromMinutes(15)),
        };
        await reminderRepository.SaveAsync(edited, TestContext.Current.CancellationToken);

        // Disable, then re-enable on a later preferences save.
        await service.SavePreferencesAndDefaultRemindersAsync(
            disabled,
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var reEnabledAtUtc = DateTimeOffset.Parse("2026-09-14T09:00:00Z");
        await service.SavePreferencesAndDefaultRemindersAsync(
            enabled,
            reEnabledAtUtc,
            TimeZoneInfo.Utc,
            TestContext.Current.CancellationToken);

        var saved = (await reminderRepository.ListAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == "default-hydration");
        Assert.True(saved.Enabled);
        Assert.Equal(new RecurrenceRule.Interval(TimeSpan.FromMinutes(15)), saved.Rule);
        Assert.Null(saved.SnoozedUntilUtc);
        Assert.NotNull(saved.NextDueUtc);
        Assert.True(
            saved.NextDueUtc > reEnabledAtUtc && saved.NextDueUtc <= reEnabledAtUtc.AddMinutes(15),
            $"expected NextDueUtc within one 15-minute interval of {reEnabledAtUtc:O} (not the shipped daily 10:00), got {saved.NextDueUtc:O}");
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
