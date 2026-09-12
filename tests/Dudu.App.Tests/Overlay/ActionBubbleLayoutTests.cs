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
        var workArea = new PixelRect(4, 8, 24, 18);
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
        var tooSmall = new PixelRect(2, 3, 15, 15);

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
        Assert.Contains("Open Settings", snapshot.ErrorMessage);
    }

    [Fact]
    public void Comfort_surface_is_absent_at_exactly_24_by_18()
    {
        var workArea = new PixelRect(9, 11, 24, 18);

        Assert.Null(ActionBubbleLayout.ArrangeComfort(workArea, new PixelPoint(999, -999)));
    }

    [Theory]
    [InlineData(24, 80)]
    [InlineData(80, 120)]
    [InlineData(280, 284)]
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
    }

    [Theory]
    [InlineData(24, 79)]
    [InlineData(23, 64)]
    [InlineData(16, 18)]
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
        foreach (var height in new[] { 1, 15, 16, 18, 64, 79, 80, 120, 283, 284 })
        {
            var workArea = new PixelRect(3, 7, width, height);
            var layout = ActionBubbleLayout.ArrangeComfort(workArea, new PixelPoint(-50, 900));
            if (layout is null) continue;

            Assert.Equal(5, layout.Actions.Count);
            Assert.True(workArea.Contains(layout.Bounds));
            Assert.All(layout.Actions, item => Assert.True(workArea.Contains(item.HitRegion)));
        }
    }
}
