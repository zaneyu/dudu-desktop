using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Audio;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class PetPresentationCoordinatorTests
{
    [Theory]
    [InlineData("greeting", AudioCueEvent.Greeting)]
    [InlineData("welcome-back", AudioCueEvent.Greeting)]
    [InlineData("celebrate", AudioCueEvent.Celebration)]
    [InlineData("note-arrival", AudioCueEvent.RemoteNote)]
    [InlineData("comfort-hug", AudioCueEvent.ManualInteraction)]
    [InlineData("petted", AudioCueEvent.Petted)]
    [InlineData("drink", AudioCueEvent.Drink)]
    [InlineData("eat", AudioCueEvent.Eat)]
    [InlineData("tantrum", AudioCueEvent.Tantrum)]
    [InlineData("drag", AudioCueEvent.Drag)]
    [InlineData("sticker-001", AudioCueEvent.Sticker)]
    [InlineData("sticker-020", AudioCueEvent.Sticker)]
    public void Presentation_audio_mapping_uses_the_expected_cue(string animationKey, AudioCueEvent expected)
    {
        var presentation = new PetPresentation(
            PetState.Idle,
            animationKey,
            null,
            null,
            false);

        Assert.Equal(expected, AudioCueSelection.ForPresentation(presentation));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("focus")]
    [InlineData("sticker-")]
    [InlineData("sticker-abc")]
    public void Presentation_audio_mapping_ignores_ambient_loops_and_unknown_keys(string animationKey)
    {
        var presentation = new PetPresentation(PetState.Ambient, animationKey, null, null, false);

        Assert.Null(AudioCueSelection.ForPresentation(presentation));
    }

    [Theory]
    [InlineData(PetState.Ambient, "greeting", AudioCueEvent.Greeting, AudioCuePriority.Interactive)]
    [InlineData(PetState.Comfort, "comfort-hug", AudioCueEvent.ManualInteraction, AudioCuePriority.Interactive)]
    [InlineData(PetState.Idle, "drink", AudioCueEvent.Drink, AudioCuePriority.Interactive)]
    [InlineData(PetState.Idle, "drag", AudioCueEvent.Drag, AudioCuePriority.Interactive)]
    [InlineData(PetState.RemoteNote, "note-arrival", AudioCueEvent.RemoteNote, AudioCuePriority.Background)]
    [InlineData(PetState.WelcomeBack, "welcome-back", AudioCueEvent.Greeting, AudioCuePriority.Background)]
    [InlineData(PetState.Reminder, "reminder", AudioCueEvent.Reminder, AudioCuePriority.Background)]
    public void User_actions_get_interactive_priority_and_arrivals_stay_background(
        PetState state,
        string animationKey,
        AudioCueEvent cue,
        AudioCuePriority expected)
    {
        var presentation = new PetPresentation(state, animationKey, null, null, false);

        Assert.Equal(expected, AudioCueSelection.PriorityFor(presentation, cue));
    }

    [Fact]
    public void Settling_events_are_silent_and_requests_are_not()
    {
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.Dismissed("note-1")));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.AmbientDismissed("greeting")));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.WelcomeBackDismissed()));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.ComfortDismissed()));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.PresentationAcknowledged()));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.PauseRequested()));
        Assert.True(AudioCueSelection.IsSettlingEvent(new PetEvent.ResumeRequested()));
        Assert.False(AudioCueSelection.IsSettlingEvent(new PetEvent.AmbientRequested("greeting")));
        Assert.False(AudioCueSelection.IsSettlingEvent(new PetEvent.ComfortRequested()));
    }

    [Fact]
    public async Task One_shot_audio_starts_with_the_visual_before_it_completes()
    {
        // Regression: the cue used to start only after the visual finished,
        // so it trailed its animation by the whole clip.
        var pet = PetStateMachine.CreateIdle();
        var visual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) => ++invocation == 1 ? visual.Task : Task.CompletedTask,
            playAudioAsync: (_, _) =>
            {
                audioStarted.TrySetResult();
                return Task.CompletedTask;
            });

        var present = coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        await audioStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(present.IsCompleted);
        visual.SetResult();
        await present;
    }

    [Fact]
    public async Task One_shot_audio_still_plays_when_the_visual_outlasts_the_maximum_duration()
    {
        // Regression: a looping or long clip (drag, eat) hit the 3 s bound
        // and its cue was skipped outright.
        var pet = PetStateMachine.CreateIdle();
        var cues = new List<AudioCueEvent?>();
        var invocation = 0;
        var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, token) => ++invocation == 1
                ? Task.Delay(Timeout.Infinite, token)
                : Task.CompletedTask,
            delayAsync: (_, _) => Task.CompletedTask,
            maximumDuration: TimeSpan.FromMilliseconds(1),
            playAudioAsync: (presentation, _) =>
            {
                cues.Add(AudioCueSelection.ForPresentation(presentation));
                return Task.CompletedTask;
            });

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        Assert.Equal([AudioCueEvent.Greeting], cues);
    }

    [Fact]
    public async Task One_shot_skips_audio_when_the_visual_fails_immediately()
    {
        var pet = PetStateMachine.CreateIdle();
        var audioCalls = 0;
        var invocation = 0;
        var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) => ++invocation == 1
                ? Task.FromException(new InvalidOperationException("visual"))
                : Task.CompletedTask,
            playAudioAsync: (_, _) =>
            {
                audioCalls++;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken));

        Assert.Equal(0, audioCalls);
    }

    [Fact]
    public async Task One_shot_starts_audio_with_the_visual_and_before_ambient_restore()
    {
        var pet = PetStateMachine.CreateIdle();
        var order = new List<string>();
        var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, _) =>
            {
                order.Add(order.Count == 0 ? "visual" : "ambient");
                return Task.CompletedTask;
            },
            playAudioAsync: (presentation, _) =>
            {
                order.Add($"audio:{AudioCueSelection.ForPresentation(presentation)}");
                return Task.CompletedTask;
            });

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        Assert.Equal(["visual", "audio:Greeting", "ambient"], order);
    }

    [Fact]
    public async Task One_shot_audio_failure_does_not_skip_acknowledgement_or_ambient_restore()
    {
        var pet = PetStateMachine.CreateIdle();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, _) =>
            {
                if (presentation.State == PetState.Idle) restored.SetResult();
                return Task.CompletedTask;
            },
            playAudioAsync: (_, _) => throw new InvalidOperationException("audio"));

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        await restored.Task;
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task One_shot_audio_cue_failure_is_reported_even_when_the_ambient_restore_call_throws()
    {
        // Regression: audioTask used to be a bare (unwrapped) task, only
        // handed to ObserveAudioAsync after the gate was released. If the
        // finally block above the release threw (e.g. the ambient-restore
        // call itself), that exception propagated straight out of
        // PresentOneShotAsync, skipping "await ObserveAudioAsync(audioTask)"
        // entirely -- a faulted audio task went unobserved and unreported.
        // The observer must be attached eagerly, under the gate, so the cue
        // failure is still reported no matter what happens afterward.
        var pet = PetStateMachine.CreateIdle();
        var invocation = 0;
        var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) =>
            {
                invocation++;
                if (invocation > 1) throw new InvalidOperationException("ambient restore failed");
                return Task.CompletedTask;
            },
            playAudioAsync: (_, _) => throw new InvalidOperationException("audio cue failed"));

        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PresentOneShotAsync(
                new PetEvent.AmbientRequested("greeting"),
                "greeting",
                TestContext.Current.CancellationToken));
            Assert.Equal("ambient restore failed", thrown.Message);

            var reported = await listener.Recorded.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains("audio cue playback failed", reported, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task One_shot_releases_the_gate_before_awaiting_a_slow_audio_cue()
    {
        // The shared gate also guards PresentationCoordinator.PresentAsync
        // on the tick path. A slow audio cue must not hold this gate, or an
        // unrelated tick-driven presentation stalls for the cue's duration.
        var pet = PetStateMachine.CreateIdle();
        using var gate = new SemaphoreSlim(1, 1);
        var audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) => Task.CompletedTask,
            gate: gate,
            playAudioAsync: async (_, _) =>
            {
                audioStarted.TrySetResult();
                await releaseAudio.Task;
            });

        var presentTask = coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        await audioStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The audio cue is still in flight, but the gate must already be
        // free for another caller (e.g. the tick loop) to acquire.
        Assert.True(await gate.WaitAsync(0, TestContext.Current.CancellationToken));
        gate.Release();

        releaseAudio.SetResult();
        await presentTask;
    }

    [Fact]
    public async Task One_shot_completes_when_resolved_playback_is_an_infinite_fallback_loop()
    {
        var pet = PetStateMachine.CreateIdle();
        var presentations = new List<PetPresentation>();
        var invocation = 0;
        var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, token) =>
            {
                presentations.Add(presentation);
                invocation++;
                if (invocation > 1) return Task.CompletedTask;
                return new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
            },
            delayAsync: (_, _) => Task.CompletedTask,
            maximumDuration: TimeSpan.FromMilliseconds(1));

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, invocation);
        Assert.Equal("greeting", presentations[0].AnimationKey);
        Assert.Equal(PetState.Idle, presentations[1].State);
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Focus_end_is_acknowledged_then_restores_authoritative_idle_state()
    {
        var pet = PetStateMachine.CreateIdle();
        pet.Handle(new PetEvent.FocusStarted("focus-1"));
        var presentations = new List<PetPresentation>();
        var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, _) =>
            {
                presentations.Add(presentation);
                return Task.CompletedTask;
            });

        await coordinator.PresentOneShotAsync(
            new PetEvent.FocusEnded("focus-1"),
            "focus-end",
            TestContext.Current.CancellationToken);

        Assert.Equal(PetState.FocusTransition, presentations[0].State);
        Assert.Equal("celebrate", presentations[0].AnimationKey);
        Assert.Equal(PetState.Idle, presentations[^1].State);
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Greeting_one_shot_dismisses_ambient_without_consuming_welcome_back()
    {
        var pet = PetStateMachine.CreateIdle();
        pet.Handle(new PetEvent.WelcomeBackRequested());
        var presentations = new List<PetPresentation>();
        var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, _) =>
            {
                presentations.Add(presentation);
                return Task.CompletedTask;
            });

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        Assert.Equal(PetState.WelcomeBack, pet.Current.State);
        Assert.Equal(PetState.WelcomeBack, presentations[^1].State);
        Assert.Equal(PetState.Idle, pet.Handle(new PetEvent.WelcomeBackDismissed()).State);
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        public TaskCompletionSource<string> Recorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null) Recorded.TrySetResult(message);
        }
    }
}
