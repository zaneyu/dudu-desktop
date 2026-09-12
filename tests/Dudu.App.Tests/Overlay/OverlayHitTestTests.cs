using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayHitTestTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(255, true)]
    public void Alpha_hit_test_uses_eight_as_the_interactive_threshold(byte alpha, bool expected)
    {
        Assert.Equal(expected, OverlayHitTest.IsInteractive(alpha));
    }

    [Fact]
    public void Alpha_hit_test_uses_current_stride_and_scale()
    {
        var pixels = new byte[3 * 8];
        pixels[2 * 8 + 1 * 4 + 3] = 8;

        Assert.False(OverlayHitTest.IsInteractive(pixels, 2, 3, 8, 2, 1, 2));
        Assert.True(OverlayHitTest.IsInteractive(pixels, 2, 3, 8, 2, 2, 4));
        Assert.False(OverlayHitTest.IsInteractive(pixels, 2, 3, 8, 2, 4, 4));
    }

    [Fact]
    public void Explicit_bubble_region_is_interactive_even_when_underlying_pixel_is_transparent()
    {
        var pixels = new byte[16];
        var regions = new[] { new PixelRect(10, 20, 30, 40) };

        Assert.True(OverlayHitTest.IsInteractive(pixels, 2, 2, 8, 1, 12, 25, regions));
        Assert.False(OverlayHitTest.IsInteractive(pixels, 2, 2, 8, 1, 9, 25, regions));
    }

    [Fact]
    public void Invalid_frame_geometry_is_transparent()
    {
        Assert.False(OverlayHitTest.IsInteractive([], 0, 1, 0, 1, 0, 0));
        Assert.False(OverlayHitTest.IsInteractive([], 1, 1, 4, 0, 0, 0));
        Assert.Equal(0, OverlayHitTest.AlphaAt([], 1, 1, 4, double.NaN, 0, 0));
    }
}
