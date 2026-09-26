using System.Runtime.InteropServices;
using Dudu.App.Overlay;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.System;

public sealed record FullscreenWindowSnapshot(
    nint ForegroundWindow,
    nint ShellWindow,
    nint DesktopWindow,
    PixelRect ExtendedFrameBounds,
    PixelRect MonitorBounds,
    PixelRect WorkArea,
    bool IsCloaked = false,
    bool IsMinimized = false,
    bool IsDuduWindow = false,
    bool IsMaximized = false,
    bool IsDesktopSurface = false);

public interface IFullscreenNativeApi
{
    nint GetForegroundWindow();
    nint GetShellWindow();
    nint GetDesktopWindow();
    bool IsIconic(nint hwnd);
    bool IsMaximized(nint hwnd);
    bool IsDuduWindow(nint hwnd);
    bool TryGetCloaked(nint hwnd, out bool cloaked);
    bool TryGetExtendedFrameBounds(nint hwnd, out PixelRect bounds);
    bool TryGetMonitorBounds(nint hwnd, out PixelRect monitorBounds, out PixelRect workArea);

    /// <summary>True for the desktop's own surfaces ("Progman", and the
    /// "WorkerW" that hosts the wallpaper after Show desktop / Win+D). They
    /// cover the whole monitor but are the desktop, not a fullscreen app.</summary>
    bool IsDesktopSurface(nint hwnd) => false;
}

public sealed class FullscreenDetector
{
    public const int EdgeTolerancePixels = 2;

    /// <summary>
    /// Consecutive native failures after which detection gives up and reports "not fullscreen".
    /// At the 250ms poll this is roughly five seconds: long enough to ride out a transient
    /// failure during a game, short enough that a machine where the query always fails does not
    /// keep the pet hidden forever.
    /// </summary>
    public const int FailClosedLimit = 20;

    private readonly IFullscreenNativeApi _native;
    private int _consecutiveFailures;

    public FullscreenDetector(IFullscreenNativeApi? native = null)
    {
        _native = native ?? new WindowsFullscreenNativeApi();
    }

    public bool IsForegroundFullscreen()
    {
        try
        {
            var foreground = _native.GetForegroundWindow();
            if (foreground == 0)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return false;
            }

            var shell = _native.GetShellWindow();
            var desktop = _native.GetDesktopWindow();
            var isCloaked = !_native.TryGetCloaked(foreground, out var cloaked) || cloaked;
            var isMinimized = _native.IsIconic(foreground);
            var isMaximized = _native.IsMaximized(foreground);
            if (!_native.TryGetExtendedFrameBounds(foreground, out var frame)
                || !_native.TryGetMonitorBounds(foreground, out var monitor, out var workArea))
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return false;
            }

            var isDudu = _native.IsDuduWindow(foreground);
            var isDesktopSurface = _native.IsDesktopSurface(foreground);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return IsForegroundFullscreen(new FullscreenWindowSnapshot(
                foreground,
                shell,
                desktop,
                frame,
                monitor,
                workArea,
                isCloaked,
                isMinimized,
                isDudu,
                isMaximized,
                isDesktopSurface));
        }
        catch
        {
            // Fail-closed, matching AppLifecycleCoordinator and the fullscreen
            // poll: a detection error is treated as fullscreen so the pet hides
            // rather than covering a game or presentation. A window that simply
            // cannot be measured (Try* returns false) is not an error. After
            // FailClosedLimit consecutive errors detection is treated as broken
            // and fails open so the pet is not hidden indefinitely; the first
            // successful query resets the count.
            return Interlocked.Increment(ref _consecutiveFailures) <= FailClosedLimit;
        }
    }

    public static bool IsForegroundFullscreen(FullscreenWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ForegroundWindow == 0
            || snapshot.ForegroundWindow == snapshot.ShellWindow
            || snapshot.ForegroundWindow == snapshot.DesktopWindow
            || snapshot.IsCloaked
            || snapshot.IsMinimized
            || snapshot.IsDuduWindow
            || snapshot.IsMaximized
            // Clicking the desktop after Show desktop (Win+D) focuses a
            // monitor-sized "WorkerW" that is not the shell window; it used
            // to read as a fullscreen app and hid the pet (and held every
            // reminder) until she clicked some other window.
            || snapshot.IsDesktopSurface
            || !snapshot.ExtendedFrameBounds.IsValid
            || !snapshot.MonitorBounds.IsValid)
        {
            return false;
        }

        return WithinTolerance(snapshot.ExtendedFrameBounds, snapshot.MonitorBounds);
    }

    internal static bool IsDesktopSurfaceClassName(string? className) =>
        string.Equals(className, "WorkerW", StringComparison.Ordinal)
        || string.Equals(className, "Progman", StringComparison.Ordinal);

    private static bool WithinTolerance(PixelRect actual, PixelRect expected)
    {
        return Math.Abs(actual.X - expected.X) <= EdgeTolerancePixels
            && Math.Abs(actual.Y - expected.Y) <= EdgeTolerancePixels
            && Math.Abs(actual.Right - expected.Right) <= EdgeTolerancePixels
            && Math.Abs(actual.Bottom - expected.Bottom) <= EdgeTolerancePixels;
    }
}

internal sealed unsafe class WindowsFullscreenNativeApi : IFullscreenNativeApi
{
    public nint GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public nint GetShellWindow() => (nint)PInvoke.GetShellWindow().Value;

    public nint GetDesktopWindow() => (nint)PInvoke.GetDesktopWindow().Value;

    public bool IsIconic(nint hwnd) => PInvoke.IsIconic(ToHwnd(hwnd));

    public bool IsMaximized(nint hwnd) => PInvoke.IsZoomed(ToHwnd(hwnd));

    public bool IsDuduWindow(nint hwnd) => OverlayWindowHost.IsDuduWindowHandle(hwnd);

    public bool IsDesktopSurface(nint hwnd)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        var length = PInvoke.GetClassName(ToHwnd(hwnd), new PWSTR(buffer), capacity);
        return length > 0
            && FullscreenDetector.IsDesktopSurfaceClassName(new string(buffer, 0, length));
    }

    public bool TryGetCloaked(nint hwnd, out bool cloaked)
    {
        cloaked = false;
        var value = 0u;
        var result = PInvoke.DwmGetWindowAttribute(
            ToHwnd(hwnd),
            DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &value,
            sizeof(uint));
        if (result.Failed)
        {
            return false;
        }

        cloaked = value != 0;
        return true;
    }

    public bool TryGetExtendedFrameBounds(nint hwnd, out PixelRect bounds)
    {
        bounds = default;
        RECT rect = default;
        var result = PInvoke.DwmGetWindowAttribute(
            ToHwnd(hwnd),
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &rect,
            (uint)sizeof(RECT));
        if (result.Failed)
        {
            return false;
        }

        bounds = new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        return bounds.IsValid;
    }

    public bool TryGetMonitorBounds(nint hwnd, out PixelRect monitorBounds, out PixelRect workArea)
    {
        monitorBounds = default;
        workArea = default;
        var monitor = PInvoke.MonitorFromWindow(ToHwnd(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.IsNull)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        monitorBounds = ToPixelRect(info.rcMonitor);
        workArea = ToPixelRect(info.rcWork);
        return monitorBounds.IsValid && workArea.IsValid;
    }

    private static HWND ToHwnd(nint hwnd) => new((void*)hwnd);

    private static PixelRect ToPixelRect(RECT rect) =>
        new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
}
