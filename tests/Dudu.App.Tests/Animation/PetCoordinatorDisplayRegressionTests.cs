using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Animation;

/// <summary>
/// Display-path regressions for the presentation coordinator: deterministic
/// ambient ordering, reporter-routed faults, visible idle fallback on bad
/// art, and unknown-dismissal diagnostics.
/// </summary>
public sealed class PetCoordinatorDisplayRegressionTests
{
    [Fact]
    public async Task Next_one_shot_cancels_stale_ambient_restore()
    {
        var pet = PetStateMachine.CreateIdle();
        var order = new List<string>();
        var restoreCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, options, token) =>
            {
                var key = presentation.State == PetState.Idle ? "idle-restore" : "one-shot";
                order.Add(key);
                if (presentation.State == PetState.Idle)
                {
                    _ = token.Register(() => restoreCancelled.TrySetResult());
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return Task.CompletedTask;
            },
            maximumDuration: TimeSpan.FromMilliseconds(50));

        var cancellationToken = TestContext.Current.CancellationToken;
        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("blink"),
            "blink",
            cancellationToken);

        // The first ambient restore is still in flight (blocked on idle).
        await coordinator.PresentOneShotAsync(
            new PetEvent.WelcomeBackRequested(),
            "welcome-back",
            cancellationToken);

        await restoreCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(
            ["one-shot", "idle-restore", "one-shot", "idle-restore"],
            order);
    }

    [Fact]
    public async Task Faulted_one_shot_reports_and_falls_back_to_visible_idle()
    {
        var pet = PetStateMachine.CreateIdle();
        var reporter = new FakeReporter();
        var played = new List<string>();
        using var coordinator = new PetPresentationCoordinator(
            pet,
            (presentation, _, _) =>
            {
                played.Add(presentation.AnimationKey);
                if (presentation.AnimationKey == "greeting")
                {
                    throw new InvalidOperationException("bad png");
                }

                return Task.CompletedTask;
            },
            maximumDuration: TimeSpan.FromMilliseconds(50),
            errorReporter: reporter);

        await coordinator.PresentOneShotAsync(
            new PetEvent.WelcomeBackRequested(),
            "welcome-back",
            TestContext.Current.CancellationToken);

        Assert.Contains("one-shot-playback", reporter.Operations);
        // The faulted greeting is followed by a visible idle present rather
        // than transparency: idle is attempted (fallback) and restored.
        Assert.Contains("idle", played);
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Unknown_dismissal_id_is_reported_not_silent()
    {
        var pet = PetStateMachine.CreateIdle();
        var reporter = new FakeReporter();
        using var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) => Task.CompletedTask,
            maximumDuration: TimeSpan.FromMilliseconds(50),
            errorReporter: reporter);

        await coordinator.PresentOneShotAsync(
            new PetEvent.RemoteNoteArrived("m-1"),
            "stale-or-wrong-id",
            TestContext.Current.CancellationToken);

        Assert.Contains("one-shot-unknown-dismissal", reporter.Operations);
        // Unknown ids are ignored: the pending note is untouched.
        Assert.True(pet.IsKnownDismissalId("m-1"));
    }

    [Fact]
    public async Task Ambient_restore_fault_reaches_reporter()
    {
        var pet = PetStateMachine.CreateIdle();
        var reporter = new FakeReporter();
        var calls = 0;
        using var coordinator = new PetPresentationCoordinator(
            pet,
            (_, _, _) =>
            {
                calls++;
                // First call is the one-shot (succeeds); the ambient restore
                // afterwards faults.
                return calls == 1
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException("restore blew up"));
            },
            maximumDuration: TimeSpan.FromMilliseconds(50),
            errorReporter: reporter);

        await coordinator.PresentOneShotAsync(
            new PetEvent.AmbientRequested("blink"),
            "blink",
            TestContext.Current.CancellationToken);

        Assert.Contains("ambient-restore", reporter.Operations);
    }

    private sealed class FakeReporter : IAppHostErrorReporter
    {
        public List<string> Operations { get; } = [];

        public void Report(string operation, Exception exception) => Operations.Add(operation);
    }
}
