using Dudu.App.Overlay;
using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class FullscreenDetectorTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Extended_frame_equal_to_monitor_bounds_is_fullscreen()
    {
        Assert.True(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, Monitor, Monitor, WorkArea)));
    }

    [Fact]
    public void Work_area_equal_window_is_not_fullscreen()
    {
        Assert.False(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, WorkArea, Monitor, WorkArea)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Cloaked_or_minimized_windows_are_never_fullscreen(bool cloaked, bool minimized, bool expected)
    {
        Assert.Equal(expected, FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, Monitor, Monitor, WorkArea, cloaked, minimized)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void Shell_desktop_and_dudu_windows_are_excluded(nint excludedHandle)
    {
        var snapshot = new FullscreenWindowSnapshot(
            excludedHandle,
            ShellWindow: 1,
            DesktopWindow: 2,
            Monitor,
            Monitor,
            WorkArea,
            IsDuduWindow: excludedHandle == 99);

        Assert.False(FullscreenDetector.IsForegroundFullscreen(snapshot));
    }

    [Fact]
    public void Two_pixel_edge_tolerance_is_accepted_but_three_pixels_is_not()
    {
        Assert.True(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, new PixelRect(2, 0, 1918, 1080), Monitor, WorkArea)));
        Assert.False(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, new PixelRect(3, 0, 1917, 1080), Monitor, WorkArea)));
    }
}
