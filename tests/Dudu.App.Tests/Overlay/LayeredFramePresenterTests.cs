using Dudu.App.Animation;
using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class LayeredFramePresenterTests
{
    [Theory]
    [InlineData(-1920, 48, 0.5)]
    [InlineData(1440, -120, 2.0)]
    public void Presentation_update_preserves_host_destination_and_scaled_size(
        int x,
        int y,
        double scale)
    {
        var state = new LayeredWindowState(new PixelRect(x, y, 640, 480), scale);

        var update = LayeredFramePresenter.CreateUpdate(state, 320, 240);

        Assert.Equal(state.Bounds, update.Bounds);
        Assert.Equal(320, update.SourceWidth);
        Assert.Equal(240, update.SourceHeight);
        Assert.Equal(640, update.DestinationWidth);
        Assert.Equal(480, update.DestinationHeight);
        Assert.Equal(scale, update.Scale);
    }

    [Fact]
    public void Invalid_presentation_state_cannot_supply_a_destination()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LayeredFramePresenter.CreateUpdate(
                new LayeredWindowState(new PixelRect(0, 0, 0, 100), 1),
                10,
                10));
    }
}
