using Dudu.App.Animation;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Xunit;

namespace Dudu.App.Tests.Presentation;

/// <summary>Regression coverage for what a firing reminder shows her.</summary>
public sealed class ReminderBubbleUxTests
{
    private static readonly DateTimeOffset DueUtc = DateTimeOffset.Parse("2026-09-17T20:00:00Z");

    [Fact]
    public async Task An_ordinary_reminder_carries_the_details_she_typed_into_the_bubble()
    {
        var gateway = new CapturingGateway();
        var sink = new ReminderDueSink(
            new SingleReminderRepository(MakeReminder("reminder-1", "Stretch", "stand up and roll your shoulders")),
            () => gateway);

        await sink.NotifyAsync(new ReminderOccurrence("reminder-1", DueUtc), TestContext.Current.CancellationToken);

        var item = Assert.IsType<DurableNotification>(gateway.LastItem);
        Assert.Equal("Stretch", item.Title);
        Assert.Equal("stand up and roll your shoulders", item.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Blank_details_leave_no_empty_body(string? details)
    {
        var gateway = new CapturingGateway();
        var sink = new ReminderDueSink(
            new SingleReminderRepository(MakeReminder("reminder-1", "Stretch", details)),
            () => gateway);

        await sink.NotifyAsync(new ReminderOccurrence("reminder-1", DueUtc), TestContext.Current.CancellationToken);

        Assert.Null(Assert.IsType<DurableNotification>(gateway.LastItem).Body);
    }

    [Fact]
    public async Task Evening_checkin_without_details_has_no_stray_leading_space()
    {
        var gateway = new CapturingGateway();
        var sink = new ReminderDueSink(
            new SingleReminderRepository(MakeReminder(
                LocalReminderDefaults.EveningCheckInId,
                LocalReminderDefaults.EveningCheckInDefaultTitle,
                details: null)),
            () => gateway);

        await sink.NotifyAsync(
            new ReminderOccurrence(LocalReminderDefaults.EveningCheckInId, DueUtc),
            TestContext.Current.CancellationToken);

        Assert.Equal("open Home to check in.", Assert.IsType<DurableNotification>(gateway.LastItem).Body);
    }

    [Fact]
    public async Task An_ordinary_reminder_names_itself_in_the_presented_bubble()
    {
        var presented = new List<PetPresentation>();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new SilentNotifications(),
            PetStateMachine.CreateIdle(),
            (presentation, _, _) =>
            {
                presented.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => DueUtc);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            TestContext.Current.CancellationToken);

        var presentation = Assert.Single(presented);
        Assert.Equal(PetState.Reminder, presentation.State);
        Assert.Equal("Stretch", presentation.BubbleTitle);
    }

    private static Reminder MakeReminder(string id, string title, string? details) =>
        new(
            id,
            title,
            details,
            true,
            new RecurrenceRule.Daily(new TimeOnly(20, 0)),
            "UTC",
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.Skip,
            DueUtc);

    private sealed class SingleReminderRepository(Reminder reminder) : IReminderRepository
    {
        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([reminder]);

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingGateway : IUnsolicitedPresentationGateway
    {
        public DurableNotification? LastItem { get; private set; }

        public Task PublishAsync(DurableNotification item, bool bypassSuppression, CancellationToken cancellationToken = default)
        {
            LastItem = item;
            return Task.CompletedTask;
        }
    }

    private sealed class SilentNotifications : INotificationService
    {
        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
