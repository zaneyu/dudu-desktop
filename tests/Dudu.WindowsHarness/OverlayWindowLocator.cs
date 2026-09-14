using System.Diagnostics;
using System.Runtime.InteropServices;
using Dudu.App.Overlay;

/// <summary>
/// Shared by <see cref="PerformanceScenario"/> and <see cref="LongRunScenario"/>: both launch a
/// published <c>Dudu.App.exe</c> and need to find its pet overlay window by class name before they
/// can start sampling it. <see cref="GetGuiResources"/> is exposed here too only because
/// <c>LongRunScenario</c> needs the GDI/USER handle counts it reads -- <c>PerformanceScenario</c>
/// never calls it.
/// </summary>
internal static class OverlayWindowLocator
{
    public static async Task<nint> WaitForOverlayWindowAsync(int processId, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * timeout.TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var handle = FindOverlayWindow(processId);
            if (handle != 0)
            {
                return handle;
            }

            await Task.Delay(100);
        }

        return 0;
    }

    public static int GetGuiResources(nint processHandle, int uiFlags) =>
        NativeMethods.GetGuiResources(processHandle, uiFlags);

    private static nint FindOverlayWindow(int processId)
    {
        var found = (nint)0;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId || !NativeMethods.IsWindowVisible(window))
            {
                return true;
            }

            Span<char> className = stackalloc char[256];
            var length = NativeMethods.GetClassName(window, ref MemoryMarshal.GetReference(className), className.Length);
            if (length == OverlayWindowHost.WindowClassName.Length
                && className[..length].SequenceEqual(OverlayWindowHost.WindowClassName))
            {
                found = window;
                return false;
            }

            return true;
        }, 0);
        return found;
    }
}

file static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint window, ref char className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetGuiResources(nint hProcess, int uiFlags);

    public delegate bool EnumWindowsCallback(nint window, nint lParam);
}
