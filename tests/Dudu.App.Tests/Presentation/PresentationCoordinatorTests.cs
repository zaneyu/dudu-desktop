using Dudu.Core.Abstractions;
using Dudu.App.Animation;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Pet;
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
