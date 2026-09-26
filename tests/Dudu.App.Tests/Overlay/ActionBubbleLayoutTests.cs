using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class ActionBubbleLayoutTests
{
    [Fact]
    public void Action_bubble_never_exposes_more_than_six_primary_actions()
    {
        var actions = Enum.GetValues<OverlayAction>();
        var workArea = new PixelRect(0, 0, 1920, 1040);

        var layout = ActionBubbleLayout.Arrange(actions, workArea, new PixelPoint(1800, 900));

        Assert.InRange(layout.PrimaryActions.Count, 1, 6);
        Assert.True(workArea.Contains(layout.Bounds));
        Assert.All(layout.PrimaryActions, placement => Assert.True(layout.Bounds.Contains(placement.HitRegion)));
        Assert.True(layout.Bounds.Contains(layout.DetailRegion));
        Assert.All(layout.PrimaryActions, placement => Assert.False(Overlaps(placement.HitRegion, layout.DetailRegion)));
    }

    [Fact]
    public void Fallback_128_pixel_surface_keeps_all_six_actions_and_reserved_detail_text()
    {
        var layout = ActionBubbleLayout.Arrange(
            OverlayCommandRouter.PrimaryActions,
            new PixelRect(0, 0, 128, 128),
            new PixelPoint(96, 112));

        Assert.Equal(6, layout.PrimaryActions.Count);
        Assert.All(layout.PrimaryActions, action =>
            Assert.False(Overlaps(action.HitRegion, layout.DetailRegion)));
    }

    [Theory]
    [InlineData(359)]
    [InlineData(300)]
    [InlineData(250)]
    [InlineData(200)]
    public void Six_action_bubble_keeps_padding_and_gaps_below_the_full_row_height(int height)
    {
        // Spacing used to drop to zero below ~360px (the full 44px-row
        // height), so the buttons touched each other and the bubble edge.
        var workArea = new PixelRect(0, 0, 384, height);

        var layout = ActionBubbleLayout.Arrange(
            OverlayCommandRouter.PrimaryActions,
            workArea,
            new PixelPoint(192, height - 20));

        Assert.Equal(6, layout.PrimaryActions.Count);
        Assert.True(workArea.Contains(layout.Bounds));
        Assert.True(layout.PrimaryActions[0].HitRegion.Y > layout.Bounds.Y, "top padding");
        for (var index = 1; index < layout.PrimaryActions.Count; index++)
        {
            Assert.True(
                layout.PrimaryActions[index].HitRegion.Y > layout.PrimaryActions[index - 1].HitRegion.Bottom,
                "rows must not touch");
        }
    }

    [Fact]
    public void Spacing_tiers_step_down_before_dropping_to_zero()
    {
        Assert.Equal((12, 8), ActionBubbleLayout.ChooseSpacing(6, 360));
        Assert.Equal((12, 8), ActionBubbleLayout.ChooseSpacing(6, 240));
        Assert.Equal((8, 6), ActionBubbleLayout.ChooseSpacing(6, 224));
        Assert.Equal((4, 4), ActionBubbleLayout.ChooseSpacing(6, 200));
        Assert.Equal((4, 4), ActionBubbleLayout.ChooseSpacing(6, 156));
        Assert.Equal((0, 0), ActionBubbleLayout.ChooseSpacing(6, 128));
    }

    [Fact]
    public void Action_bubble_deduplicates_and_falls_back_to_pet()
    {
        var layout = ActionBubbleLayout.Arrange(
            [OverlayAction.Tasks, OverlayAction.Tasks],
            new PixelRect(0, 0, 640, 480),
            new PixelPoint(320, 240));

        Assert.Equal([OverlayAction.Tasks], layout.PrimaryActions.Select(item => item.Action));
        var empty = ActionBubbleLayout.Arrange([], new PixelRect(0, 0, 640, 480), new PixelPoint(320, 240));
        Assert.Equal(OverlayAction.Pet, Assert.Single(empty.PrimaryActions).Action);
    }

    [Fact]
    public void Action_bubble_keeps_hit_regions_safe_in_a_tiny_work_area()
    {
        var workArea = new PixelRect(4, 8, 24, 48);
        var layout = ActionBubbleLayout.Arrange(
            OverlayCommandRouter.PrimaryActions,
            workArea,
            new PixelPoint(-100, 999));

        Assert.True(workArea.Contains(layout.Bounds));
        Assert.All(layout.PrimaryActions, item =>
        {
            Assert.True(item.HitRegion.Width > 0);
            Assert.True(item.HitRegion.Height > 0);
            Assert.True(layout.Bounds.Contains(item.HitRegion));
            Assert.True(workArea.Contains(item.HitRegion));
        });
    }

    [Fact]
    public void Action_and_comfort_surfaces_return_none_when_area_is_smaller_than_one_safe_row()
    {
        var tooSmall = new PixelRect(2, 3, 15, 47);

        Assert.Null(ActionBubbleLayout.TryArrange(
            OverlayCommandRouter.PrimaryActions,
            tooSmall,
            new PixelPoint(4, 4)));
        Assert.Null(ActionBubbleLayout.ArrangeComfort(tooSmall, new PixelPoint(4, 4)));
    }

    [Fact]
    public void Geometry_failure_keeps_a_status_surface_and_an_accessible_settings_message()
    {
        using var surface = new OverlayActionSurfaceController();

        surface.Open(new PixelRect(0, 0, 15, 15), new PixelPoint(4, 4));

        var snapshot = surface.CreateRenderSnapshot();
        Assert.Equal(OverlayActionSurfaceKind.Status, snapshot.Kind);
        Assert.Empty(snapshot.Actions);
        Assert.Contains("open settings", snapshot.ErrorMessage);
    }

    [Fact]
    public void Comfort_surface_is_absent_at_exactly_24_by_18()
    {
        var workArea = new PixelRect(9, 11, 24, 111);

        Assert.Null(ActionBubbleLayout.ArrangeComfort(workArea, new PixelPoint(999, -999)));
    }

    [Theory]
    [InlineData(24, 112)]
    [InlineData(80, 152)]
    [InlineData(280, 316)]
    public void Every_non_empty_comfort_surface_contains_exactly_five_safe_actions(
        int width,
        int height)
    {
        var workArea = new PixelRect(9, 11, width, height);

        var layout = Assert.IsType<ComfortBubbleArrangement>(
            ActionBubbleLayout.ArrangeComfort(workArea, new PixelPoint(999, -999)));

        Assert.True(workArea.Contains(layout.Bounds));
        Assert.Equal(5, layout.Actions.Count);
        Assert.All(layout.Actions, item => Assert.True(workArea.Contains(item.HitRegion)));
        Assert.All(layout.Actions, item => Assert.False(Overlaps(item.HitRegion, layout.DetailRegion)));
    }

    [Theory]
    [InlineData(24, 111)]
    [InlineData(23, 96)]
    [InlineData(16, 47)]
    public void Comfort_surface_returns_none_when_five_safe_rows_cannot_fit(int width, int height)
    {
        Assert.Null(ActionBubbleLayout.ArrangeComfort(
            new PixelRect(0, 0, width, height),
            new PixelPoint(0, 0)));
    }

    [Fact]
    public void Comfort_layout_invariant_is_exactly_five_or_none_across_constrained_sizes()
    {
        foreach (var width in new[] { 1, 15, 16, 24, 80, 279, 280 })
        foreach (var height in new[] { 1, 47, 48, 95, 96, 111, 112, 152, 315, 316 })
        {
            var workArea = new PixelRect(3, 7, width, height);
            var layout = ActionBubbleLayout.ArrangeComfort(workArea, new PixelPoint(-50, 900));
            if (layout is null) continue;

            Assert.Equal(5, layout.Actions.Count);
            Assert.True(workArea.Contains(layout.Bounds));
            Assert.All(layout.Actions, item => Assert.True(workArea.Contains(item.HitRegion)));
        }
    }

    [Fact]
    public void Render_snapshot_maps_window_hit_geometry_into_the_source_frame()
    {
        using var surface = new OverlayActionSurfaceController();
        surface.Open(new PixelRect(0, 0, 512, 512), new PixelPoint(384, 420));

        var windowSnapshot = surface.CreateRenderSnapshot();
        var sourceSnapshot = surface.CreateRenderSnapshot(new PixelSize(256, 256));

        Assert.Equal(windowSnapshot.Actions.Count, sourceSnapshot.Actions.Count);
        for (var index = 0; index < windowSnapshot.Actions.Count; index++)
        {
            var window = windowSnapshot.Actions[index].HitRegion;
            var source = sourceSnapshot.Actions[index].HitRegion;
            Assert.InRange(Math.Abs(source.X * 2 - window.X), 0, 1);
            Assert.InRange(Math.Abs(source.Y * 2 - window.Y), 0, 1);
            Assert.InRange(Math.Abs(source.Width * 2 - window.Width), 0, 2);
            Assert.InRange(Math.Abs(source.Height * 2 - window.Height), 0, 2);
        }
    }

    [Fact]
    public void Open_surface_reflows_when_the_viewport_changes()
    {
        using var surface = new OverlayActionSurfaceController();
        surface.Open(new PixelRect(0, 0, 512, 512), new PixelPoint(400, 440));

        surface.UpdateViewport(new PixelRect(0, 0, 256, 384));

        var arrangement = Assert.IsType<ActionBubbleArrangement>(surface.Arrangement);
        Assert.True(new PixelRect(0, 0, 256, 384).Contains(arrangement.Bounds));
        Assert.All(arrangement.PrimaryActions, item =>
            Assert.True(new PixelRect(0, 0, 256, 384).Contains(item.HitRegion)));
    }

    [Fact]
    public async Task Concurrent_reflow_snapshot_and_hit_reads_remain_internally_valid()
    {
        using var surface = new OverlayActionSurfaceController();
        surface.Open(new PixelRect(0, 0, 512, 512), new PixelPoint(384, 420));

        var readers = Enumerable.Range(0, 4).Select(workerIndex => Task.Run(() =>
        {
            _ = workerIndex;
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var snapshot = surface.CreateRenderSnapshot(new PixelSize(256, 256));
                Assert.All(snapshot.Actions, action =>
                    Assert.True(snapshot.Bounds!.Value.Contains(action.HitRegion)));
                _ = surface.Contains(new PixelPoint(iteration % 512, iteration % 512));
            }
        }, TestContext.Current.CancellationToken));
        var writer = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var size = iteration % 2 == 0 ? 512 : 384;
                surface.UpdateViewport(new PixelRect(0, 0, size, size));
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(readers.Append(writer));
    }

    private static bool Overlaps(PixelRect first, PixelRect second) =>
        first.X < second.Right
        && first.Right > second.X
        && first.Y < second.Bottom
        && first.Bottom > second.Y;
}
