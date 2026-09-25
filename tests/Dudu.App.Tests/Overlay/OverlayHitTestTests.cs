using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayHitTestTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(255, true)]
    public void Alpha_hit_test_uses_thirty_two_as_the_interactive_threshold(byte alpha, bool expected)
    {
        Assert.Equal(expected, OverlayHitTest.IsInteractive(alpha));
    }

    [Fact]
    public void Interactive_threshold_rejects_soft_halo_fringe()
    {
        // Pinned: composited pet frames carry a soft AA fringe in the
        // 8..31 alpha band outside the solid art; it must not arm clicks.
        Assert.Equal(32, OverlayHitTest.InteractiveAlphaThreshold);
        Assert.False(OverlayHitTest.IsInteractive(8));
        Assert.False(OverlayHitTest.IsInteractive(31));
        Assert.True(OverlayHitTest.IsInteractive(32));
    }

    [Fact]
    public void Alpha_hit_test_uses_current_stride_and_scale()
    {
        var pixels = new byte[3 * 8];
        pixels[2 * 8 + 1 * 4 + 3] = 32;

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

    [Theory]
    [InlineData(0.5, 1, 1)]
    [InlineData(2.0, 3, 3)]
    public void Scaled_client_coordinates_map_to_the_current_source_frame(
        double scale,
        int clientX,
        int clientY)
    {
        var pixels = new byte[4 * 4 * 4];
        var sourceX = clientX == 1 && scale == 0.5 ? 2 : 1;
        var sourceY = clientY == 1 && scale == 0.5 ? 2 : 1;
        pixels[sourceY * 16 + sourceX * 4 + 3] = 32;

        Assert.True(OverlayHitTest.IsInteractive(
            pixels,
            width: 4,
            height: 4,
            stride: 16,
            clientWidth: scale == 0.5 ? 2 : 8,
            clientHeight: scale == 0.5 ? 2 : 8,
            clientX,
            clientY));
    }

    [Fact]
    public void Bubble_regions_are_also_interpreted_in_current_client_coordinates()
    {
        var pixels = new byte[4 * 4 * 4];
        var bubbles = new[] { new PixelRect(6, 8, 4, 4) };

        Assert.True(OverlayHitTest.IsInteractive(
            pixels,
            width: 4,
            height: 4,
            stride: 16,
            clientWidth: 8,
            clientHeight: 8,
            clientX: 7,
            clientY: 9,
            explicitHitRegions: bubbles));
    }

    [Fact]
    public void Invalid_frame_geometry_is_transparent()
    {
        Assert.False(OverlayHitTest.IsInteractive([], 0, 1, 0, 1, 0, 0));
        Assert.False(OverlayHitTest.IsInteractive([], 1, 1, 4, 0, 0, 0));
        Assert.Equal(0, OverlayHitTest.AlphaAt([], 1, 1, 4, double.NaN, 0, 0));
    }
}
