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

    [Fact]
    public void Presentation_audio_mapping_ignores_unknown_animation()
    {
        var presentation = new PetPresentation(PetState.Ambient, "idle", null, null, false);

        Assert.Null(AudioCueSelection.ForPresentation(presentation));
    }

    [Fact]
    public async Task One_shot_plays_audio_after_visual_and_before_ambient_restore()
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
