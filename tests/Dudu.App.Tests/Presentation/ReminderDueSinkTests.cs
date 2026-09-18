using Dudu.App.Animation;
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
        Assert.Equal("how was your day, ada? a little space to reflect. your check-in stays on this device. open Home to check in.", item.Body);
        Assert.Null(item.AnimationKey);
        Assert.Equal(DueUtc.AddDays(1), item.ExpiresUtc);
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

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("time to wind down. goodnight, ada.", item.Body);
        Assert.Equal("sleep", item.AnimationKey);
        Assert.Null(item.ExpiresUtc);
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
            new ReminderOccurrence(LocalReminderDefaults.BedtimeId, DueUtc),
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
            MakeReminder(LocalReminderDefaults.EveningCheckInId, "how was your day, ada?"));
        var gateway = new RecordingGateway();
        var sink = new ReminderDueSink(reminders, () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("how was your day, ada?", item.Title);
        Assert.Equal("how was your day, ada? a little space to reflect. your check-in stays on this device. open Home to check in.", item.Body);
    }

    private static Reminder MakeReminder(
        string id,
        string title,
        bool enabled = true) =>
        new(
            id,
            title,
            "a little space to reflect. your check-in stays on this device.",
            enabled,
            new RecurrenceRule.Daily(new TimeOnly(20, 0)),
            "UTC",
            QuietHoursBehavior.DeliverImmediately,
            MissedOccurrencePolicy.Skip,
            DueUtc);

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
