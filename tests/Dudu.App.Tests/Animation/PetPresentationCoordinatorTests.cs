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
                order.Add(presentation.State == PetState.Ambient ? "ambient" : "visual");
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
            playAudioAsync: (_, _) => Task.FromException(new InvalidOperationException("audio")));

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            TestContext.Current.CancellationToken);

        await restored.Task;
        Assert.Equal(PetState.Idle, pet.Current.State);
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
}
