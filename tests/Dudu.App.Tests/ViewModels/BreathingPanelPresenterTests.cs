using Dudu.App.Overlay;
using Dudu.App.Tests.Overlay;
using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>"Breathe with me" on Home: starting it shows the phase text,
/// Stop sends Close, hides the panel, and the breathing task ends as a
/// normal cancellation (which Home shows no error for).</summary>
public sealed class BreathingPanelPresenterTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Starting_shows_the_phase_and_stop_hides_it_with_a_normal_cancellation()
    {
        var (router, entered) = CreateBlockingRouter();
        using var presenter = new BreathingPanelPresenter(router);
        Assert.False(presenter.IsVisible);

        var breathing = router.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(presenter.IsVisible);
        Assert.True(presenter.IsBreathing);
        Assert.False(string.IsNullOrWhiteSpace(presenter.PhaseText));
        Assert.Equal("breathe in for 4", presenter.Instruction);

        await presenter.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(presenter.IsVisible);
        Assert.False(presenter.IsBreathing);
        Assert.False(router.ComfortPanel.IsOpen);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => breathing.WaitAsync(Bound, TestContext.Current.CancellationToken));
        // The cancellation's own clean-up keeps the panel closed.
        Assert.False(presenter.IsVisible);
    }

    [Fact]
    public async Task A_finished_exercise_stays_visible_as_done_until_closed()
    {
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var presenter = new BreathingPanelPresenter(router);
        var phases = new List<string>();
        presenter.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BreathingPanelPresenter.PhaseText)) phases.Add(presenter.PhaseText);
        };

        await router.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);

        Assert.True(presenter.IsVisible);
        Assert.Equal(BreathingPanelPresenter.PhaseTextFor(BreathVisualPhase.Complete), presenter.PhaseText);
        Assert.Contains(BreathingPanelPresenter.PhaseTextFor(BreathVisualPhase.Inhale), phases);
        Assert.Contains(BreathingPanelPresenter.PhaseTextFor(BreathVisualPhase.Exhale), phases);

        await presenter.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(presenter.IsVisible);
    }

    [Fact]
    public async Task Leaving_home_stops_only_a_running_exercise()
    {
        var router = new OverlayCommandRouter(OverlayTestFeatureContext.Create(), (_, _) => Task.CompletedTask);
        using var presenter = new BreathingPanelPresenter(router);
        var changes = 0;
        router.ComfortPanelChanged += (_, _) => changes++;

        // Nothing running (e.g. a tiny hug just played): no Close is sent,
        // so nothing is dismissed.
        await presenter.StopIfBreathingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, changes);
        Assert.Equal(ComfortPanelState.Closed, router.ComfortPanel);

        var (running, entered) = CreateBlockingRouter();
        using var runningPresenter = new BreathingPanelPresenter(running);
        var breathing = running.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await runningPresenter.StopIfBreathingAsync(TestContext.Current.CancellationToken);

        Assert.False(running.IsBreathing);
        Assert.False(runningPresenter.IsVisible);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => breathing.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detached_presenter_ignores_changes_and_resyncs_when_attached_again()
    {
        var (router, entered) = CreateBlockingRouter();
        using var presenter = new BreathingPanelPresenter(router);
        presenter.Detach();

        var breathing = router.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.False(presenter.IsVisible);

        presenter.Attach();
        Assert.True(presenter.IsVisible);

        await presenter.StopAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => breathing.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_router_the_panel_never_shows_and_stop_is_a_no_op()
    {
        using var presenter = new BreathingPanelPresenter(router: null);

        await presenter.StopAsync(TestContext.Current.CancellationToken);
        await presenter.StopIfBreathingAsync(TestContext.Current.CancellationToken);

        Assert.False(presenter.IsVisible);
        Assert.False(presenter.IsBreathing);
        Assert.Equal(string.Empty, presenter.PhaseText);
    }

    [Fact]
    public void Every_phase_has_friendly_text()
    {
        Assert.All(Enum.GetValues<BreathVisualPhase>(), phase =>
            Assert.False(string.IsNullOrWhiteSpace(BreathingPanelPresenter.PhaseTextFor(phase))));
    }

    /// <summary>A router whose breathing delay blocks until cancelled, so the
    /// exercise is observably "running"; entered completes on the first
    /// delay (after the first phase was published).</summary>
    private static (OverlayCommandRouter Router, TaskCompletionSource Entered) CreateBlockingRouter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (_, _) => Task.CompletedTask,
            async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        return (router, entered);
    }
}
