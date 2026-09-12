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
        });
    }
}
