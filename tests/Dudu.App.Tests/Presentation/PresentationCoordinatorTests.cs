using Dudu.Core.Abstractions;
using Dudu.App.Animation;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Pet;
using Dudu.Core.Notes;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PresentationCoordinatorTests
{
    [Fact]
    public async Task PublishAsync_does_not_double_present_an_item_that_is_currently_presenting()
    {
        var pet = PetStateMachine.CreateIdle();
        var playbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playCount = 0;
        var notifications = new RecordingNotificationService();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            pet,
            async (_, _, _) =>
            {
                Interlocked.Increment(ref playCount);
                await playbackGate.Task;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));
        var item = DurableNotification.Reminder("reminder-1", "Stretch");

        var firstPublish = coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        // The first PublishAsync call runs synchronously up to the blocked
        // playback await, so by this point the item is already marked
        // "currently presenting" — this second call for the identical key
        // must be a no-op rather than a second presentation.
        await coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);

        playbackGate.SetResult();
        await firstPublish;

        Assert.Equal(1, playCount);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public async Task PublishAsync_ignores_an_item_that_is_already_queued()
    {
        var pet = PetStateMachine.CreateIdle();
        var notifications = new RecordingNotificationService();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => true,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));
        var item = DurableNotification.Reminder("reminder-1", "Stretch");

        await coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        await coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
    }

    [Fact]
    public async Task PublishAsync_is_deduped_against_an_item_TickAsync_is_currently_presenting()
    {
        var pet = PetStateMachine.CreateIdle();
        var notifications = new RecordingNotificationService();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var playbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playCount = 0;
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            async (_, _, _) =>
            {
                Interlocked.Increment(ref playCount);
                await playbackGate.Task;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));
        var item = DurableNotification.Reminder("reminder-1", "Stretch");

        // Suppressed: the item is queued, not presented.
        await coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);

        // No longer suppressed: TickAsync dequeues it and starts presenting
        // (blocked on playbackGate), marking it "currently presenting".
        quiet = false;
        var tick = coordinator.TickAsync(CancellationToken.None);

        // A concurrent duplicate — even a bypass one — must be deduped
        // against what TickAsync is currently presenting, not queued again
        // or presented a second time.
        await coordinator.PublishAsync(item, bypassSuppression: true, CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);

        playbackGate.SetResult();
        await tick;

        Assert.Equal(1, playCount);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public async Task Failed_presentation_is_requeued_for_a_later_tick()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
    }

    [Fact]
    public async Task Eligible_ambient_tick_selects_and_presents_a_local_note_through_the_gateway()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
        var notes = new RecordingLocalNoteRepository(
            new LocalLoveNote("note-1", "You are doing great."));
        var preferences = Preferences.Default with { LocalNoteDailyLimit = 1 };
        var scheduler = new AmbientScheduler(
            clock,
            new FixedRandomSource(),
            preferences.AmbientMinimumInterval,
            preferences.QuietHours);
        var selector = new LocalNoteSelector(notes, clock, new FixedRandomSource(), preferences);
        PetPresentation? presented = null;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (presentation, _, _) =>
            {
                presented = presentation;
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            ambientScheduler: scheduler,
            localNoteSelector: selector);

        // The scheduler holds its first ambient moment for one minimum interval
        // after construction, so advance past eligibility before ticking.
        clock.Advance(preferences.AmbientMinimumInterval);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(presented);
        Assert.Equal(PetState.Ambient, presented.State);
        Assert.Equal("You are doing great.", presented.BubbleBody);
        Assert.Equal(1, notes.ShownCount);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    private sealed class RecordingLocalNoteRepository(LocalLoveNote note) : ILocalNoteRepository
    {
        public int ShownCount { get; private set; }

        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>([note]);

        public Task<int> CountUnsolicitedShownAsync(DateOnly localDate, CancellationToken cancellationToken) =>
            Task.FromResult(ShownCount);

        public Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> TryRecordShownAsync(
            string noteId,
            DateTimeOffset shownUtc,
            DateOnly localDate,
            int dailyLimit,
            bool unsolicited,
            CancellationToken cancellationToken)
        {
            ShownCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public int ReminderCalls { get; private set; }
        public int RemoteNoteCalls { get; private set; }

        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
        {
            ReminderCalls++;
            return Task.CompletedTask;
        }

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken)
        {
            RemoteNoteCalls++;
            return Task.CompletedTask;
        }
    }
}
