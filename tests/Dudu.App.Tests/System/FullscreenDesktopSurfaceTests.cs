using Dudu.App.Overlay;
using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

/// <summary>Clicking the desktop after Show desktop (Win+D) focuses a
/// monitor-sized "WorkerW" window; it must not read as a fullscreen app.</summary>
public sealed class FullscreenDesktopSurfaceTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void A_monitor_sized_desktop_surface_is_not_fullscreen()
    {
        Assert.False(FullscreenDetector.IsForegroundFullscreen(
            new FullscreenWindowSnapshot(42, 1, 2, Monitor, Monitor, WorkArea, IsDesktopSurface: true)));
    }

    [Fact]
    public void The_live_detector_asks_whether_the_foreground_is_a_desktop_surface()
    {
        var native = new DesktopNativeApi { DesktopSurface = true };
        var detector = new FullscreenDetector(native);

        Assert.False(detector.IsForegroundFullscreen());

        native.DesktopSurface = false;
        Assert.True(detector.IsForegroundFullscreen());
    }

    [Theory]
    [InlineData("WorkerW", true)]
    [InlineData("Progman", true)]
    [InlineData("workerw", false)]
    [InlineData("UnityWndClass", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_shell_desktop_window_classes_count_as_desktop(string? className, bool expected)
    {
        Assert.Equal(expected, FullscreenDetector.IsDesktopSurfaceClassName(className));
    }

    private sealed class DesktopNativeApi : IFullscreenNativeApi
    {
        public bool DesktopSurface { get; set; }
        public nint GetForegroundWindow() => 42;
        public nint GetShellWindow() => 1;
        public nint GetDesktopWindow() => 2;
        public bool IsIconic(nint hwnd) => false;
        public bool IsMaximized(nint hwnd) => false;
        public bool IsDuduWindow(nint hwnd) => false;
        public bool IsDesktopSurface(nint hwnd) => DesktopSurface;
        public bool TryGetCloaked(nint hwnd, out bool cloaked) { cloaked = false; return true; }
        public bool TryGetExtendedFrameBounds(nint hwnd, out PixelRect bounds) { bounds = Monitor; return true; }
        public bool TryGetMonitorBounds(nint hwnd, out PixelRect monitorBounds, out PixelRect workArea)
        {
            monitorBounds = Monitor;
            workArea = WorkArea;
            return true;
        }
    }
}
