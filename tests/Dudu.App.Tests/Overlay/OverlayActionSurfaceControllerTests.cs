using Dudu.App.Overlay;
using Dudu.Core.Assets;
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
}
