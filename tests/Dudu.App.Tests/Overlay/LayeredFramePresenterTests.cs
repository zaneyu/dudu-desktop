using Dudu.App.Animation;
using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class LayeredFramePresenterTests
{
    [Fact]
    public void Presented_overlay_regions_scale_with_the_exact_source_and_client_geometry()
    {
        var regions = LayeredFramePresenter.ScaleRegionsToClient(
            [new PixelRect(64, 32, 64, 48)],
            sourceWidth: 256,
            sourceHeight: 128,
            clientWidth: 512,
            clientHeight: 384);

        Assert.Equal(new PixelRect(128, 96, 128, 144), Assert.Single(regions));
    }

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

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Blend_configuration_keeps_per_pixel_alpha_without_squaring_opacity(float opacity)
    {
        var blend = LayeredFramePresenter.CreateBlendConfiguration(opacity);

        Assert.Equal(byte.MaxValue, blend.SourceConstantAlpha);
        Assert.Equal(1, blend.AlphaFormat);
    }
}
