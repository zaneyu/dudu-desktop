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

    [Fact]
    public void Maximized_window_is_not_treated_as_exclusive_fullscreen()
    {
        Assert.False(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(
                42,
                1,
                2,
                Monitor,
                Monitor,
                WorkArea,
                IsMaximized: true)));
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
    [Fact]
    public void Native_query_failure_fails_closed_as_fullscreen()
    {
        Assert.True(new FullscreenDetector(new ThrowingNativeApi()).IsForegroundFullscreen());
    }

    [Fact]
    public void Persistent_native_failure_eventually_fails_open_so_the_pet_is_not_hidden_forever()
    {
        var detector = new FullscreenDetector(new ThrowingNativeApi());
        for (var i = 0; i < FullscreenDetector.FailClosedLimit; i++)
        {
            Assert.True(detector.IsForegroundFullscreen());
        }

        Assert.False(detector.IsForegroundFullscreen());
        Assert.False(detector.IsForegroundFullscreen());
    }

    [Fact]
    public void A_successful_query_resets_the_failure_count()
    {
        var native = new ThrowingNativeApi();
        var detector = new FullscreenDetector(native);
        for (var i = 0; i < FullscreenDetector.FailClosedLimit + 3; i++)
        {
            detector.IsForegroundFullscreen();
        }

        native.Fail = false;
        Assert.False(detector.IsForegroundFullscreen());
        native.Fail = true;
        Assert.True(detector.IsForegroundFullscreen());
    }

    private sealed class ThrowingNativeApi : IFullscreenNativeApi
    {
        public bool Fail { get; set; } = true;
        public nint GetForegroundWindow() => Fail ? throw new InvalidOperationException("native failed") : 0;
        public nint GetShellWindow() => 0;
        public nint GetDesktopWindow() => 0;
        public bool IsIconic(nint hwnd) => false;
        public bool IsMaximized(nint hwnd) => false;
        public bool IsDuduWindow(nint hwnd) => false;
        public bool TryGetCloaked(nint hwnd, out bool cloaked) { cloaked = false; return true; }
        public bool TryGetExtendedFrameBounds(nint hwnd, out PixelRect bounds) { bounds = default; return false; }
        public bool TryGetMonitorBounds(nint hwnd, out PixelRect monitorBounds, out PixelRect workArea)
        {
            monitorBounds = default;
            workArea = default;
            return false;
        }
    }
}
