using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayActionSurfaceControllerTests
{
    private static readonly PixelRect Viewport = new(0, 0, 640, 480);
    private static readonly PixelPoint Anchor = new(320, 400);

    [Theory]
    [InlineData(OverlayAction.Pet)]
    [InlineData(OverlayAction.DrinkWater)]
    public async Task Reaction_actions_close_the_bubble_before_the_animation_plays(OverlayAction action)
    {
        // The one-shot reaction is awaited for its whole duration; closing
        // only afterwards played it hidden underneath the open bubble.
        OverlayActionSurfaceKind? kindWhilePlaying = null;
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var surface = new OverlayActionSurfaceController();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: async (_, _, _) =>
        {
            kindWhilePlaying = surface.Kind;
            playing.TrySetResult();
            await finish.Task;
        });
        surface.Bind(new OverlayCommandRouter(context, (_, _) => Task.CompletedTask));
        surface.Open(Viewport, Anchor);
        var presented = surface.CreateRenderSnapshot().Actions.Single(item => item.PrimaryAction == action);

        var dispatch = surface.HandlePresentedActionAsync(presented, TestContext.Current.CancellationToken);
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Closed, kindWhilePlaying);
        finish.TrySetResult();
        Assert.True(await dispatch);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
    }

    [Fact]
    public async Task Failed_reaction_after_closing_still_reports_a_status_error()
    {
        using var surface = new OverlayActionSurfaceController();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (_, _, _) =>
            Task.FromException(new InvalidOperationException("reaction failed")));
        surface.Bind(new OverlayCommandRouter(context, (_, _) => Task.CompletedTask));
        surface.Open(Viewport, Anchor);
        var pet = surface.CreateRenderSnapshot().Actions.Single(item => item.PrimaryAction == OverlayAction.Pet);

        await surface.HandlePresentedActionAsync(pet, TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Status, surface.Kind);
        Assert.Equal("reaction failed", surface.ErrorMessage);
    }

    [Theory]
    [InlineData(ComfortAction.TinyHug)]
    [InlineData(ComfortAction.TakeAFiveMinuteBreak)]
    public async Task Hug_collapses_the_comfort_panel_before_it_plays(ComfortAction comfortAction)
    {
        OverlayActionSurfaceKind? kindWhilePlaying = null;
        bool? panelOpenWhilePlaying = null;
        OverlayCommandRouter? router = null;
        using var surface = new OverlayActionSurfaceController();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (petEvent, _, _) =>
        {
            if (petEvent is PetEvent.ComfortRequested)
            {
                kindWhilePlaying = surface.Kind;
                panelOpenWhilePlaying = router!.ComfortPanel.IsOpen;
            }

            return Task.CompletedTask;
        });
        router = new OverlayCommandRouter(context, (_, _) => Task.CompletedTask);
        surface.Bind(router);
        surface.Open(Viewport, Anchor);
        await surface.HandlePresentedActionAsync(
            surface.CreateRenderSnapshot().Actions.Single(item => item.PrimaryAction == OverlayAction.ComfortMe),
            TestContext.Current.CancellationToken);
        Assert.Equal(OverlayActionSurfaceKind.Comfort, surface.Kind);

        await surface.HandlePresentedActionAsync(
            surface.CreateRenderSnapshot().Actions.Single(item => item.ComfortAction == comfortAction),
            TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Closed, kindWhilePlaying);
        Assert.False(panelOpenWhilePlaying);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
    }

    [Theory]
    [InlineData(ComfortAction.TinyHug)]
    [InlineData(ComfortAction.ReadALoveNote)]
    [InlineData(ComfortAction.BreatheWithMe)]
    public async Task Any_comfort_action_interrupts_breathing_instead_of_firing_late(ComfortAction next)
    {
        // Only Close used to cancel "breathe with me" (up to 60 s); every
        // other click queued behind it and they all fired late at once.
        var breathingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var breathingRuns = 0;
        var navigations = new List<string>();
        using var surface = new OverlayActionSurfaceController();
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (destination, _) =>
            {
                navigations.Add(destination);
                return Task.CompletedTask;
            },
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref breathingRuns) == 1)
                {
                    breathingEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            });
        surface.Bind(router);
        surface.Open(Viewport, Anchor);
        await surface.HandlePresentedActionAsync(
            surface.CreateRenderSnapshot().Actions.Single(item => item.PrimaryAction == OverlayAction.ComfortMe),
            TestContext.Current.CancellationToken);
        var snapshot = surface.CreateRenderSnapshot();
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.Enqueue(surface, snapshot.Actions.Single(item => item.ComfortAction == ComfortAction.BreatheWithMe));
        await breathingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.Enqueue(surface, snapshot.Actions.Single(item => item.ComfortAction == next));
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(reported);
        Assert.False(router.IsBreathing);
        if (next == ComfortAction.ReadALoveNote) Assert.Equal(["notes"], navigations);
        // Restarting: the first run was cancelled after its first delay and
        // the second ran all 6 inhale/exhale cycles (12 delays) right away.
        if (next == ComfortAction.BreatheWithMe) Assert.Equal(1 + 12, breathingRuns);
    }

    [Fact]
    public async Task Pointer_clicks_on_comfort_actions_also_interrupt_breathing()
    {
        var breathingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var surface = new OverlayActionSurfaceController();
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (_, _) => Task.CompletedTask,
            async (_, cancellationToken) =>
            {
                breathingEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        surface.Bind(router);
        surface.Open(Viewport, Anchor);
        await surface.HandlePresentedActionAsync(
            surface.CreateRenderSnapshot().Actions.Single(item => item.PrimaryAction == OverlayAction.ComfortMe),
            TestContext.Current.CancellationToken);
        var comfort = surface.ComfortArrangement!.Actions;
        using var queue = new OverlayActionDispatchQueue(_ => { });

        queue.Enqueue(surface, Center(comfort.Single(item => item.Action == ComfortAction.BreatheWithMe).HitRegion));
        await breathingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.Enqueue(surface, Center(comfort.Single(item => item.Action == ComfortAction.TinyHug).HitRegion));
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(router.IsBreathing);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
    }

    [Fact]
    public void Interrupting_when_nothing_is_breathing_leaves_the_comfort_panel_alone()
    {
        var router = new OverlayCommandRouter(OverlayTestFeatureContext.Create(), (_, _) => Task.CompletedTask);
        router.OpenComfortPanel();
        var before = router.ComfortPanel;

        Assert.False(router.CancelActiveBreathing());
        Assert.Equal(before, router.ComfortPanel);
    }

    private static PixelPoint Center(PixelRect rectangle) =>
        new(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
}
