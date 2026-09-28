using Dudu.Core.Abstractions;
using Dudu.App.Animation;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.Hosting;
using Dudu.Core.Pet;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PresentationCoordinatorTests
{
    private const string NoteOne = "11111111-1111-4111-8111-111111111111";
    private const string NoteTwo = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void Notification_audio_mapping_uses_the_expected_cue()
    {
        var item = DurableNotification.RemoteNote(Guid.NewGuid().ToString("D"));

        Assert.Equal(AudioCueEvent.RemoteNote, AudioCueSelection.ForNotification(item));
    }

    [Theory]
    [InlineData("sticker-003", AudioCueEvent.Sticker)]
    [InlineData("sticker-030", AudioCueEvent.Sticker)]
    [InlineData("drink", AudioCueEvent.ManualInteraction)]
    [InlineData("blink", AudioCueEvent.ManualInteraction)]
    [InlineData("sleep", AudioCueEvent.ManualInteraction)]
    [InlineData("greeting", AudioCueEvent.ManualInteraction)]
    [InlineData("celebrate", AudioCueEvent.ManualInteraction)]
    public void Ambient_audio_maps_stickers_to_sticker_and_everything_else_to_manual_interaction_at_background_priority(
        string animationKey,
        AudioCueEvent expected)
    {
        // Reproduces the old local-note arm of ForNotification exactly;
        // ForPresentation would map drink to an interactive cue and
        // blink/sleep to silence.
        var cue = AudioCueSelection.ForAmbient(animationKey);

        Assert.Equal(expected, cue);
        Assert.Equal(AudioCuePriority.Background, AudioCueSelection.PriorityFor(cue));
    }

    [Fact]
    public async Task Audio_starts_with_the_visual_before_it_completes()
    {
        // Regression: the cue used to start only after the visual finished
        // (up to the 5 s playback bound), so it trailed its animation.
        var visual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => visual.Task,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            playAudioAsync: (_, _) =>
            {
                audioStarted.TrySetResult();
                return Task.CompletedTask;
            });

        var publish = coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        await audioStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(publish.IsCompleted);
        visual.SetResult();
        await publish;
    }

    [Fact]
    public async Task A_requeued_retry_does_not_replay_the_cue_that_already_started()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var visualCalls = 0;
        var audioCalls = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                // Fails after starting (the cue has already begun by then).
                if (Interlocked.Increment(ref visualCalls) == 1)
                {
                    await Task.Yield();
                    throw new InvalidOperationException("playback failed");
                }
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            playAudioAsync: (_, _) =>
            {
                Interlocked.Increment(ref audioCalls);
                return Task.CompletedTask;
            });

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(0, policy.QueuedCount);
        Assert.Equal(2, visualCalls);
        Assert.Equal(1, audioCalls);
    }

    [Fact]
    public async Task A_visual_that_fails_immediately_gets_no_cue()
    {
        var audioCalls = 0;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            playAudioAsync: (_, _) =>
            {
                audioCalls++;
                return Task.CompletedTask;
            });

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(0, audioCalls);
    }

    [Fact]
    public async Task Audio_cue_failure_is_reported_as_audio_cue_playback()
    {
        var reporter = new RecordingErrorReporter();
        var failure = new InvalidOperationException("audio");
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            errorReporter: reporter,
            playAudioAsync: (_, _) => throw failure);

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Contains(reporter.Reports, report => report.Operation == "audio-cue-playback"
            && ReferenceEquals(report.Exception, failure));
    }

    [Fact]
    public async Task Audio_starts_with_visual_and_a_failure_does_not_block_notification_delivery()
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(["visual", "audio", "notification"], order);
        Assert.Equal(1, notifications.RemoteNoteCalls);
    }

    [Fact]
    public async Task A_failed_toast_does_not_veto_a_successful_animations_result()
    {
        // Finding 15: PresentAsync used to AND the toast call's result into
        // `succeeded`, so a toast that failed after the pet animation had
        // already played turned an otherwise-successful presentation into a
        // failure -- and both PublishAsync and TickAsync requeue-and-replay
        // a failed item from the top, so this would replay the animation a
        // second time later even though it already played fine once. The
        // toast failure must still be reported, just not flip the
        // presentation's own outcome.
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var reporter = new RecordingErrorReporter();
        var played = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new FailingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            errorReporter: reporter);

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, played);
        // A failed presentation is requeued by PublishAsync's caller; a
        // successful one is not -- this is the observable proof that the
        // toast failure did not flip the outcome.
        Assert.Equal(0, policy.QueuedCount);
        Assert.Contains(reporter.Reports, r => r.Exception is InvalidOperationException ex
            && ex.Message == "simulated toast failure");

        // The animation must not replay on a later tick.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, played);
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        // The cue is still in flight (releaseAudio has not been set), yet
        // the notification already went out.
        Assert.Equal(["visual", "audio-started", "notification"], order);
        Assert.Equal(1, notifications.RemoteNoteCalls);

        releaseAudio.SetResult();
        await audioFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["visual", "audio-started", "notification", "audio-finished"], order);
    }

    [Fact]
    public async Task Direct_audio_starts_without_waiting_for_the_visual()
    {
        // Regression: the direct (Home / overlay menu) path waited for the
        // visual to finish, or 2 s on a loop, before the cue started.
        var visual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var observation = WindowsCompanionProductionComposition.ObserveDirectAudioAsync(
            visual.Task,
            () =>
            {
                order.Add("audio");
                return Task.CompletedTask;
            });

        Assert.Equal(["audio"], order);
        await observation;
        Assert.False(visual.Task.IsCompleted);
    }

    [Fact]
    public async Task Direct_audio_is_skipped_for_a_failed_or_cancelled_visual_and_swallows_cue_failures()
    {
        var calls = 0;
        Task Play() { calls++; return Task.CompletedTask; }

        await WindowsCompanionProductionComposition.ObserveDirectAudioAsync(
            Task.FromException(new InvalidOperationException("visual")), Play);
        await WindowsCompanionProductionComposition.ObserveDirectAudioAsync(
            Task.FromCanceled(new CancellationToken(canceled: true)), Play);
        Assert.Equal(0, calls);

        await WindowsCompanionProductionComposition.ObserveDirectAudioAsync(
            Task.CompletedTask,
            () => Task.FromException(new InvalidOperationException("audio")));
    }

    [Fact]
    public void Audio_startup_line_flags_silent_packs_without_paths()
    {
        var loud = new AudioCue("tata-lala/tata-lala-01.wav", 500, "hash") { PeakLevel = 0.5 };
        var silent = new AudioCue("dudu-lalala/lalala-01.wav", 500, "hash") { PeakLevel = 0 };
        var catalog = new AudioCatalog([
            new AudioSoundPack("tata-lala", [loud]),
            new AudioSoundPack("dudu-lalala", [silent]),
        ]);

        var (level, message) = WindowsCompanionProductionComposition.DescribeAudioStartup(
            catalog, loaded: true, Preferences.Default);

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, level);
        Assert.Contains("player=winmm", message);
        Assert.Contains("cues=2", message);
        Assert.Contains("silentCues=1", message);
        Assert.Contains("silentPacks=[dudu-lalala]", message);
        Assert.Contains("sounds=on", message);
        Assert.DoesNotContain(".wav", message);
        Assert.DoesNotContain("/", message);
        Assert.DoesNotContain("\\", message);

        var healthy = WindowsCompanionProductionComposition.DescribeAudioStartup(
            new AudioCatalog([new AudioSoundPack("tata-lala", [loud])]), loaded: true, Preferences.Default);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, healthy.Level);

        var fallback = WindowsCompanionProductionComposition.DescribeAudioStartup(
            new AudioCatalog([new AudioSoundPack("tata-lala", [])]), loaded: false, Preferences.Default);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, fallback.Level);
        Assert.Contains("player=no-op", fallback.Message);
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
        var item = DurableNotification.RemoteNote(NoteOne);

        var firstPublish = coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        // The first PublishAsync call runs synchronously up to the blocked
        // playback await, so by this point the item is already marked
        // "currently presenting" — this second call for the identical key
        // must be a no-op rather than a second presentation.
        await coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);

        playbackGate.SetResult();
        await firstPublish;

        Assert.Equal(1, playCount);
        Assert.Equal(1, notifications.RemoteNoteCalls);
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
        var item = DurableNotification.RemoteNote(NoteOne);

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
        var item = DurableNotification.RemoteNote(NoteOne);

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
        Assert.Equal(1, notifications.RemoteNoteCalls);
    }

    [Fact]
    public async Task Item_is_not_presented_as_a_drag_or_meal_that_started_before_it_reached_the_pet()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var pet = PetStateMachine.CreateIdle();
        var playCount = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            pet,
            (_, _, _) =>
            {
                Interlocked.Increment(ref playCount);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1));

        // Bypass skips the snapshot check, so only the under-gate re-check
        // can hold it back -- the same spot a tick-path race lands on.
        pet.Handle(new PetEvent.DragStarted());
        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: true,
            CancellationToken.None);
        pet.Handle(new PetEvent.DragEnded());
        pet.Handle(new PetEvent.EatingStarted("meal-1"));
        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteTwo),
            bypassSuppression: true,
            CancellationToken.None);

        Assert.Equal(0, playCount);
        Assert.Equal(2, policy.QueuedCount);
        Assert.Equal(PetState.Eating, pet.Current.State);
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
    }

    [Fact]
    public async Task Failed_presentation_does_not_dismiss_the_item_from_the_pet_state()
    {
        // Regression for H1(a): PresentAsync's finally block used to call
        // Dismissed(item.Id) unconditionally, even when playback failed —
        // so a note that failed to play vanished from the pet's pending
        // set (and thus from Select()'s ranking) even though the policy
        // still requeues it for a later tick. The note must stay latched
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
        Assert.Equal(PetState.RemoteNote, pet.Current.State);
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(0, played);
        Assert.Equal(PetState.Idle, pet.Current.State);
        Assert.Equal(1, policy.QueuedCount);
        Assert.Equal(1, notifications.RemoteNoteCalls);

        // Un-hiding releases the queued item through the normal tick path —
        // the toast must not fire a second time for the same item.
        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
        Assert.Equal(1, notifications.RemoteNoteCalls);
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        // The immediate toast for the held item.
        Assert.Equal(1, notifications.RemoteNoteCalls);

        coordinator.SetUserVisible(true);

        // First release attempt: playback fails, item is requeued.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);
        Assert.Equal(1, notifications.RemoteNoteCalls);

        // Retry succeeds — must not toast a second time in total.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Equal(1, notifications.RemoteNoteCalls);
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
    public async Task A_second_note_still_shows_while_the_first_is_acknowledged()
    {
        // The coalesced-card design (PetStateMachine.PendingCount) must
        // survive the per-item acknowledgement: dismissing the item that was
        // just shown should not touch a different note still pending.
        var pet = PetStateMachine.CreateIdle();
        pet.Handle(new PetEvent.RemoteNoteArrived(NoteTwo));
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
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(PetState.RemoteNote, pet.Current.State);
        Assert.Equal(1, pet.PendingCount);
    }

    [Fact]
    public async Task Eligible_ambient_tick_presents_a_sticker_with_no_note_text()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
        // Slot 5 of the ambient pool is the sticker sentinel.
        var scheduler = new AmbientScheduler(
            clock,
            new ConstantRandomSource(5),
            Preferences.Default.AmbientMinimumInterval);
        PetPresentation? presented = null;
        var audio = new List<AudioCueEvent>();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (presentation, _, _) =>
            {
                presented ??= presentation;
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            ambientScheduler: scheduler,
            utcNow: () => clock.UtcNow,
            playAudioAsync: (cue, _) =>
            {
                audio.Add(cue);
                return Task.CompletedTask;
            },
            availableStickerKeys: ["sticker-001"],
            localTimeZone: TimeZoneInfo.Utc);

        // The scheduler holds its first ambient moment for one minimum interval
        // after construction, so advance past eligibility before ticking.
        clock.Advance(Preferences.Default.AmbientMinimumInterval);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(presented);
        Assert.Equal(PetState.Ambient, presented.State);
        Assert.Equal("sticker-001", presented.AnimationKey);
        Assert.True(string.IsNullOrEmpty(presented.BubbleBody));
        Assert.Equal(new[] { AudioCueEvent.Sticker }, audio);
    }

    [Fact]
    public async Task Ambient_is_capped_at_three_played_moments_per_local_day_and_resets_next_day()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T08:00:00Z"));
        var interval = Preferences.Default.AmbientMinimumInterval;
        var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), interval);
        var played = new List<PetPresentation>();
        var audio = new List<AudioCueEvent>();
        var pet = PetStateMachine.CreateIdle();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (presentation, _, _) =>
            {
                played.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            ambientScheduler: scheduler,
            utcNow: () => clock.UtcNow,
            playAudioAsync: (cue, _) =>
            {
                audio.Add(cue);
                return Task.CompletedTask;
            },
            localTimeZone: TimeZoneInfo.Utc);

        for (var tick = 0; tick < 10; tick++)
        {
            clock.Advance(interval * 2);
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            PresentationCoordinator.MaxUnsolicitedAmbientPerDay,
            played.Count(p => p.State == PetState.Ambient));
        Assert.Equal(3, played.Count(p => p.State == PetState.Ambient));
        // A non-sticker ambient clip (the fixed random source picks blink)
        // gets the manual-interaction cue, once per played moment.
        Assert.Equal(
            new[] { AudioCueEvent.ManualInteraction, AudioCueEvent.ManualInteraction, AudioCueEvent.ManualInteraction },
            audio);
        Assert.Equal(PetState.Idle, pet.Current.State);

        clock.Advance(TimeSpan.FromDays(1));
        clock.Advance(interval * 2);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, played.Count(p => p.State == PetState.Ambient));
    }

    [Fact]
    public async Task Suppressed_or_failed_ambient_does_not_count_toward_the_cap()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T08:00:00Z"));
        var interval = Preferences.Default.AmbientMinimumInterval;
        var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), interval);
        var played = new List<PetPresentation>();
        var failedAmbientAttempts = 0;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (presentation, _, _) =>
            {
                if (presentation.State == PetState.Ambient && failedAmbientAttempts < 2)
                {
                    failedAmbientAttempts++;
                    throw new InvalidOperationException("playback failed");
                }

                played.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            ambientScheduler: scheduler,
            utcNow: () => clock.UtcNow,
            errorReporter: new RecordingErrorReporter(),
            localTimeZone: TimeZoneInfo.Utc);

        // Suppressed (locked) ticks long past eligibility play nothing.
        coordinator.SetSessionLocked(true);
        for (var tick = 0; tick < 3; tick++)
        {
            clock.Advance(interval * 2);
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(played);
        Assert.Equal(0, failedAmbientAttempts);

        coordinator.SetSessionLocked(false);
        for (var tick = 0; tick < 10; tick++)
        {
            clock.Advance(interval * 2);
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }

        // Still the same local day: two failed moments, then the full three.
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T08:00:00Z").Date, clock.UtcNow.Date);
        Assert.Equal(2, failedAmbientAttempts);
        Assert.Equal(3, played.Count(p => p.State == PetState.Ambient));
    }

    [Fact]
    public async Task Held_remote_note_waits_the_silent_interval_after_an_ambient_moment()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
        var silentInterval = TimeSpan.FromMinutes(10);
        var policy = new PresentationPolicy(silentInterval);
        var scheduler = new AmbientScheduler(
            clock,
            new FixedRandomSource(),
            Preferences.Default.AmbientMinimumInterval);
        var played = new List<PetPresentation>();
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (presentation, _, _) =>
            {
                played.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            ambientScheduler: scheduler,
            utcNow: () => clock.UtcNow,
            localTimeZone: TimeZoneInfo.Utc);

        clock.Advance(Preferences.Default.AmbientMinimumInterval);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(played, p => p.State == PetState.Ambient);

        // A partner note arrives while fullscreen holds it, then fullscreen ends.
        coordinator.SetFullscreen(true);
        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(NoteOne),
            bypassSuppression: false,
            TestContext.Current.CancellationToken);
        coordinator.SetFullscreen(false);
        Assert.Equal(1, policy.QueuedCount);

        // Inside the silent interval after the ambient moment: still held.
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, policy.QueuedCount);
        Assert.DoesNotContain(played, p => p.State == PetState.RemoteNote);

        // Once the interval has passed it is released.
        clock.Advance(silentInterval);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Contains(played, p => p.State == PetState.RemoteNote);
    }

    [Fact]
    public async Task Neglected_pet_throws_one_tantrum_through_the_tick_then_returns_to_idle()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
        var affection = new AffectionTracker(clock);
        var pet = PetStateMachine.CreateIdle();
        var played = new List<PetPresentation>();
        var audio = new List<AudioCueEvent>();
        var locked = false;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (presentation, _, _) =>
            {
                played.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => clock.UtcNow,
            playAudioAsync: (cue, _) =>
            {
                audio.Add(cue);
                return Task.CompletedTask;
            },
            affection: affection);
        coordinator.SetSessionLocked(false);

        async Task TickForAsync(TimeSpan duration)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromSeconds(30))
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                coordinator.SetSessionLocked(locked);
                await coordinator.TickAsync(TestContext.Current.CancellationToken);
            }
        }

        // Locked time never counts as neglect.
        locked = true;
        await TickForAsync(TimeSpan.FromHours(3));
        Assert.Empty(played);

        // The first unlocked tick only starts the active stretch.
        locked = false;
        await TickForAsync(AffectionTracker.NeglectThreshold + TimeSpan.FromMinutes(1));

        var tantrum = Assert.Single(played, item => item.AnimationKey == "tantrum");
        Assert.Equal(PetState.Ambient, tantrum.State);
        Assert.Equal(PetStateMachine.TantrumBubble, tantrum.BubbleTitle);
        Assert.Equal(PetState.Idle, played[^1].State);
        Assert.Equal(PetState.Idle, pet.Current.State);

        // At most one per cooldown, and petting resets the clock entirely.
        await TickForAsync(AffectionTracker.TantrumCooldown - TimeSpan.FromMinutes(1));
        Assert.Single(played, item => item.AnimationKey == "tantrum");
        affection.RecordPet();
        await TickForAsync(TimeSpan.FromMinutes(60));
        Assert.Single(played, item => item.AnimationKey == "tantrum");
    }

    [Theory]
    [InlineData("eating")]
    [InlineData("dragging")]
    public async Task Busy_pet_suppresses_ambient_and_tantrum_and_holds_a_remote_note(string busyKind)
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
        var affection = new AffectionTracker(clock);
        var pet = PetStateMachine.CreateIdle();
        if (busyKind == "eating")
        {
            pet.Handle(new PetEvent.EatingStarted("meal-1"));
        }
        else
        {
            pet.Handle(new PetEvent.DragStarted());
        }

        var preferences = Preferences.Default;
        var scheduler = new AmbientScheduler(
            clock,
            new FixedRandomSource(),
            preferences.AmbientMinimumInterval,
            preferences.QuietHours);
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var played = new List<PetPresentation>();
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            pet,
            (presentation, _, _) =>
            {
                played.Add(presentation);
                return Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => clock.UtcNow,
            ambientScheduler: scheduler,
            affection: affection,
            localTimeZone: TimeZoneInfo.Utc);
        coordinator.SetUserVisible(true);
        coordinator.SetSessionLocked(false);

        // Two hours of ticks: long past the ambient scheduler's first eligible
        // moment and past the tantrum's neglect threshold, so only the busy
        // pet can be what holds them back.
        for (var tick = 0; tick < 240; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(played, item => item.State is PetState.Ambient);
        Assert.DoesNotContain(played, item => item.AnimationKey == "tantrum");
        Assert.Equal(TimeSpan.Zero, affection.NeglectedFor);

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
            bypassSuppression: false,
            TestContext.Current.CancellationToken);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, policy.QueuedCount);
        Assert.DoesNotContain(played, item => item.State is PetState.RemoteNote);
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

    /// <summary>Always answers <paramref name="value"/>, clamped into range.</summary>
    private sealed class ConstantRandomSource(int value) : IRandomSource
    {
        public int Next(int exclusiveMax) => Math.Min(value, exclusiveMax - 1);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        private readonly List<string>? _order;

        public RecordingNotificationService(List<string>? order = null) => _order = order;

        public int RemoteNoteCalls { get; private set; }

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken)
        {
            _order?.Add("notification");
            RemoteNoteCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingNotificationService : INotificationService
    {
        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated toast failure");
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = [];

        public void Report(string operation, Exception exception) => Reports.Add((operation, exception));
    }
}
