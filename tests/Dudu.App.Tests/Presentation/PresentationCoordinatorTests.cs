using Dudu.Core.Abstractions;
using Dudu.App.Animation;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.Hosting;
using Dudu.Core.Pet;
using Dudu.Core.Notes;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PresentationCoordinatorTests
{
    [Theory]
    [InlineData(PresentationItemKind.RemoteNote, AudioCueEvent.RemoteNote)]
    [InlineData(PresentationItemKind.Reminder, AudioCueEvent.Reminder)]
    [InlineData(PresentationItemKind.LocalNote, AudioCueEvent.ManualInteraction)]
    public void Notification_audio_mapping_uses_the_expected_cue(
        PresentationItemKind kind,
        AudioCueEvent expected)
    {
        var item = kind switch
        {
            PresentationItemKind.RemoteNote => DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
            PresentationItemKind.Reminder => DurableNotification.Reminder("reminder-1", "Stretch"),
            PresentationItemKind.LocalNote => DurableNotification.LocalNote(
                new LocalLoveNote("note-1", "hello"),
                "greeting"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(expected, AudioCueSelection.ForNotification(item));
    }

    [Fact]
    public async Task Audio_runs_after_visual_and_a_failure_does_not_block_notification_delivery()
    {
        var order = new List<string>();
        var notifications = new RecordingNotificationService(order);
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { order.Add("visual"); return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            playAudioAsync: (_, _) =>
            {
                order.Add("audio");
                throw new InvalidOperationException("audio");
            });

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(["visual", "audio", "notification"], order);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public async Task Audio_cue_is_fire_and_forget_and_does_not_delay_notification_delivery()
    {
        // Regression: PresentAsync used to await the audio cue before
        // showing the Windows toast on the 30 s tick, so a slow cue delayed
        // delivery by up to the cue's own bound. The cue must be
        // fire-and-forget -- the notification goes out once the cue has
        // started, not once it has finished.
        var order = new List<string>();
        var notifications = new RecordingNotificationService(order);
        var releaseAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { order.Add("visual"); return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            playAudioAsync: async (_, _) =>
            {
                order.Add("audio-started");
                await releaseAudio.Task;
                order.Add("audio-finished");
                audioFinished.TrySetResult();
            });

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        // The cue is still in flight (releaseAudio has not been set), yet
        // the notification already went out.
        Assert.Equal(["visual", "audio-started", "notification"], order);
        Assert.Equal(1, notifications.ReminderCalls);

        releaseAudio.SetResult();
        await audioFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["visual", "audio-started", "notification", "audio-finished"], order);
    }

    [Fact]
    public async Task Direct_audio_waits_for_visual_playback_completion()
    {
        var visual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var observation = WindowsCompanionProductionComposition.ObserveDirectAudioAfterVisualAsync(
            visual.Task,
            () =>
            {
                order.Add("audio");
                return Task.CompletedTask;
            });

        Assert.Empty(order);
        visual.SetResult();
        await observation;

        Assert.Equal(["audio"], order);
    }

    [Fact]
    public void Audio_manifest_uses_the_resolved_assets_root()
    {
        var assetsRoot = Path.Combine("publish", "Assets");

        Assert.Equal(
            Path.Combine(assetsRoot, "Audio", "private-dudu", "manifest.json"),
            WindowsCompanionProductionComposition.ResolveAudioManifestPath(assetsRoot));
    }

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
    public async Task Failed_presentation_does_not_dismiss_the_item_from_the_pet_state()
    {
        // Regression for H1(a): PresentAsync's finally block used to call
        // Dismissed(item.Id) unconditionally, even when playback failed —
        // so a reminder that failed to play vanished from the pet's pending
        // set (and thus from Select()'s ranking) even though the policy
        // still requeues it for a later tick. The reminder must stay latched
        // in the state machine until a presentation actually succeeds.
        var pet = PetStateMachine.CreateIdle();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            pet,
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
        Assert.Equal(PetState.Reminder, pet.Current.State);
        Assert.Equal(1, pet.PendingCount);
    }

    [Fact]
    public async Task Presentation_held_while_user_hidden_still_fires_its_toast()
    {
        // Regression for H1(b): the pet overlay is suppressed while the user
        // has hidden Dudu from the tray, exactly like fullscreen/pause, but a
        // Windows toast has nothing to do with the on-screen overlay and must
        // still fire immediately instead of waiting for un-hide.
        var pet = PetStateMachine.CreateIdle();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var notifications = new RecordingNotificationService();
        var played = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));
        coordinator.SetUserVisible(false);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(0, played);
        Assert.Equal(PetState.Idle, pet.Current.State);
        Assert.Equal(1, policy.QueuedCount);
        Assert.Equal(1, notifications.ReminderCalls);

        // Un-hiding releases the queued item through the normal tick path —
        // the toast must not fire a second time for the same item.
        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public async Task An_item_expired_while_held_does_not_swallow_a_later_same_key_toast()
    {
        // Regression: an item toasted-while-held that then expires before
        // ever reaching a real presentation (PresentationPolicy.Decide
        // silently purges it from the queue) used to leave its key latched
        // in _toastedWhileHeldIds forever. A future item recurring under the
        // same key (e.g. a daily routine reminder) would then find that
        // stale entry and skip its own Windows toast.
        var pet = PetStateMachine.CreateIdle();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var notifications = new RecordingNotificationService();
        var now = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => now);
        coordinator.SetUserVisible(false);

        var expiring = DurableNotification.Reminder(
            "reminder-1",
            "Stretch",
            expiresUtc: now.AddMinutes(1));
        await coordinator.PublishAsync(expiring, bypassSuppression: false, CancellationToken.None);

        // The immediate toast for the held item.
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Equal(1, policy.QueuedCount);

        // Let the queued item expire and get silently purged by a tick —
        // it never reaches a real presentation.
        now = now.AddMinutes(2);
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Equal(1, notifications.ReminderCalls);

        // A new item recurring under the same key (unsuppressed this time)
        // must still get its own toast.
        coordinator.SetUserVisible(true);
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(2, notifications.ReminderCalls);
    }

    [Fact]
    public async Task A_failed_attempt_after_a_held_toast_does_not_toast_twice_on_retry()
    {
        // Regression: the toasted-while-held marker used to be consumed
        // (removed) even when the presentation attempt failed, so the
        // requeued retry no longer saw it and fired a second Windows toast
        // for the same item.
        var pet = PetStateMachine.CreateIdle();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var notifications = new RecordingNotificationService();
        var attempt = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            (_, _, _) =>
            {
                attempt++;
                return attempt == 1
                    ? Task.FromException(new InvalidOperationException("playback failed"))
                    : Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));
        coordinator.SetUserVisible(false);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        // The immediate toast for the held item.
        Assert.Equal(1, notifications.ReminderCalls);

        coordinator.SetUserVisible(true);

        // First release attempt: playback fails, item is requeued.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);
        Assert.Equal(1, notifications.ReminderCalls);

        // Retry succeeds — must not toast a second time in total.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public async Task Reminder_presentation_is_acknowledged_so_the_pet_returns_to_idle()
    {
        // Regression for B5: before this fix, only LocalNote presentations
        // were acknowledged in PresentAsync's finally block, so a due
        // reminder's id sat in the state machine's pending set forever and
        // Select() kept ranking PetState.Reminder above everything else.
        var pet = PetStateMachine.CreateIdle();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task RemoteNote_presentation_is_acknowledged_so_the_pet_returns_to_idle()
    {
        var pet = PetStateMachine.CreateIdle();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task A_second_reminder_still_shows_while_the_first_is_acknowledged()
    {
        // The coalesced-card design (PetStateMachine.PendingCount) must
        // survive the per-item acknowledgement: dismissing the item that was
        // just shown should not touch a different reminder still pending.
        var pet = PetStateMachine.CreateIdle();
        pet.Handle(new PetEvent.ReminderDue("reminder-2"));
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(PetState.Reminder, pet.Current.State);
        Assert.Equal(1, pet.PendingCount);
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
        private readonly List<string>? _order;

        public RecordingNotificationService(List<string>? order = null) => _order = order;

        public int ReminderCalls { get; private set; }
        public int RemoteNoteCalls { get; private set; }

        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
        {
            _order?.Add("notification");
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
