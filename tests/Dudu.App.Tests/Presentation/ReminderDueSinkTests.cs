using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class ReminderDueSinkTests
{
    private static readonly DateTimeOffset DueUtc =
        DateTimeOffset.Parse("2026-09-17T20:00:00Z");

    [Fact]
    public async Task Evening_checkin_shows_its_prompt_details_directing_home()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.EveningCheckInId, "how was your day, ada?"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("a little space to reflect. your check-in stays on this device. open Home to check in.", item.Body);
        Assert.Null(item.AnimationKey);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T00:00:00Z"), item.ExpiresUtc);
        Assert.False(gateway.LastBypass);
    }

    [Fact]
    public async Task Bedtime_shows_goodnight_details_with_the_sleep_animation()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.BedtimeId, "shuijiaojiao, ada"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc),
            TestContext.Current.CancellationToken);

        // No profile is wired, so the legacy "ada" details personalise down to the
        // neutral copy (an unknown recipient) rather than keep saying "ada".
        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("time to wind down. goodnight.", item.Body);
        Assert.Equal("sleep", item.AnimationKey);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T00:00:00Z"), item.ExpiresUtc);
        Assert.False(gateway.LastBypass);
    }

    [Fact]
    public async Task Expired_routine_occurrence_is_dropped_before_it_can_queue()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.BedtimeId, "shuijiaojiao, ada"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc.AddDays(-1)),
            TestContext.Current.CancellationToken);
        await gateway.Coordinator.PublishAsync(
            gateway.LastItem!,
            bypassSuppression: gateway.LastBypass,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, gateway.Policy.QueuedCount);
        Assert.Equal(0, gateway.PlayCount);
    }

    [Fact]
    public async Task A_disabled_reminder_is_skipped_entirely()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.BedtimeId, "shuijiaojiao, ada", enabled: false));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc),
            TestContext.Current.CancellationToken);

        Assert.Null(gateway.LastItem);
    }

    [Fact]
    public async Task A_generic_reminder_keeps_its_existing_behavior()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder("reminder-1", "Stretch"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence("reminder-1", DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Null(item.Body);
        Assert.Null(item.AnimationKey);
        Assert.Null(item.ExpiresUtc);
        Assert.True(gateway.LastBypass);
    }

    [Fact]
    public async Task Toast_title_never_contains_reflection_content()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.EveningCheckInId, LocalReminderDefaults.EveningCheckInDefaultTitle));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day?", item.Title);
        Assert.Equal("a little space to reflect. your check-in stays on this device. open Home to check in.", item.Body);
    }

    [Fact]
    public async Task Evening_checkin_title_takes_the_saved_recipient_name()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(
                LocalReminderDefaults.EveningCheckInId,
                LocalReminderDefaults.EveningCheckInDefaultTitle));
        var gateway = new RecordingGateway();
        var profiles = new RecordingProfileRepository(new Profile("mei", OnboardingComplete: true));
        var sink = new ReminderDueSink(reminders, () => gateway, profiles: profiles);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day, mei?", item.Title);
    }

    [Fact]
    public async Task Bedtime_drops_the_name_clause_naturally_when_the_profile_has_none()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(
                LocalReminderDefaults.BedtimeId,
                LocalReminderDefaults.BedtimeDefaultTitle,
                details: LocalReminderDefaults.BedtimeDefaultDetails));
        var gateway = new RecordingGateway();
        var profiles = new RecordingProfileRepository(new Profile(string.Empty, OnboardingComplete: true));
        var sink = new ReminderDueSink(reminders, () => gateway, profiles: profiles);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("shuijiaojiao", item.Title);
        Assert.Equal("time to wind down. goodnight.", item.Body);
    }

    [Fact]
    public async Task Reminders_already_stored_with_the_legacy_baked_in_name_still_personalise()
    {
        // Rows persisted by the version that hardcoded "ada" into the stored title
        // are recognised by their exact legacy text so an untouched install still
        // gets personalised copy instead of always saying "ada".
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.EveningCheckInId, "how was your day, ada?"));
        var gateway = new RecordingGateway();
        var profiles = new RecordingProfileRepository(new Profile("mei", OnboardingComplete: true));
        var sink = new ReminderDueSink(reminders, () => gateway, profiles: profiles);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day, mei?", item.Title);
    }

    [Fact]
    public async Task Legacy_ada_text_falls_back_to_neutral_copy_when_no_profile_is_wired()
    {
        // No IProfileRepository wired at all (recipientName is always null): the
        // legacy text is still a recognised default, so it personalises down to the
        // neutral copy rather than keep saying "ada" to a possibly-different person.
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.EveningCheckInId, "how was your day, ada?"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day?", item.Title);
    }

    [Fact]
    public async Task A_user_edited_title_is_never_personalised()
    {
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.BedtimeId, "lights out bb"));
        var gateway = new RecordingGateway();
        var profiles = new RecordingProfileRepository(new Profile("mei", OnboardingComplete: true));
        var sink = new ReminderDueSink(reminders, () => gateway, profiles: profiles);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("lights out bb", item.Title);
    }

    [Fact]
    public async Task A_transient_profile_read_failure_still_publishes_with_the_neutral_title()
    {
        // The occurrence is already durably committed by the time this sink runs; a
        // profile-read failure (e.g. a transient SQLITE_BUSY) must not drop the
        // whole notification. It falls back to the neutral copy and reports the
        // failure separately instead of throwing.
        var reminders = new RecordingReminderRepository(
            MakeReminder(LocalReminderDefaults.EveningCheckInId, LocalReminderDefaults.EveningCheckInDefaultTitle));
        var gateway = new RecordingGateway();
        var failure = new InvalidOperationException("profiles table busy");
        var profiles = new ThrowingProfileRepository(failure);
        var reporter = new RecordingErrorReporter();
        var sink = new ReminderDueSink(reminders, () => gateway, reporter, profiles);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day?", item.Title);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal("reminder-notify-profile", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    private static Reminder MakeReminder(
        string id,
        string title,
        bool enabled = true,
        string? details = null) =>
        new(
            id,
            title,
            details ?? (id == LocalReminderDefaults.BedtimeId
                ? "time to wind down. goodnight, ada."
                : "a little space to reflect. your check-in stays on this device."),
            enabled,
            new RecurrenceRule.Daily(new TimeOnly(20, 0)),
            "UTC",
            QuietHoursBehavior.DeliverImmediately,
            MissedOccurrencePolicy.Skip,
            DueUtc);

    private sealed class RecordingProfileRepository(Profile? profile) : IProfileRepository
    {
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingProfileRepository(Exception failure) : IProfileRepository
    {
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromException<Profile?>(failure);

        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record ErrorReport(string Operation, Exception Exception);

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<ErrorReport> Reports { get; } = [];

        public void Report(string operation, Exception exception) =>
            Reports.Add(new ErrorReport(operation, exception));
    }

    private sealed class RecordingReminderRepository(params Reminder[] reminders) : IReminderRepository
    {
        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(reminders);

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingGateway : IUnsolicitedPresentationGateway
    {
        public RecordingGateway()
        {
            Policy = new(TimeSpan.Zero);
            Coordinator = new PresentationCoordinator(
                Policy,
                new RecordingNotificationService(),
                PetStateMachine.CreateIdle(),
                (_, _, _) =>
                {
                    PlayCount++;
                    return Task.CompletedTask;
                },
                () => AnimationOptions.Default,
                isQuietHours: () => false,
                pauseState: () => PauseState.None,
                petGate: new SemaphoreSlim(1, 1),
                utcNow: () => DueUtc);
        }

        public PresentationPolicy Policy { get; }

        public PresentationCoordinator Coordinator { get; }

        public DurableNotification? LastItem { get; private set; }

        public bool LastBypass { get; private set; }

        public int PlayCount { get; private set; }

        public Task PublishAsync(
            DurableNotification item,
            bool bypassSuppression,
            CancellationToken cancellationToken = default)
        {
            LastItem = item;
            LastBypass = bypassSuppression;
            return Coordinator.PublishAsync(item, bypassSuppression, cancellationToken);
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public Task ShowReminderAsync(
            string reminderId,
            string title,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowRemoteNoteArrivalAsync(
            Guid messageId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
