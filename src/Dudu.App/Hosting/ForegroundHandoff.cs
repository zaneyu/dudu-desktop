using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dudu.App.Hosting;

/// <summary>
/// A second launch (clicking Dudu while it already runs) forwards "open Home"
/// to the primary over the activation pipe and exits. The primary is a
/// background process, so Windows' foreground lock kept the Home window it
/// opened behind whatever she was using (or only flashed its taskbar button):
/// she clicked again and again and "settings would not open". The secondary
/// was launched by her click and may still take the foreground, so it hands
/// that right to the primary before sending the request. Best effort: every
/// failure only means the old behavior.
/// </summary>
internal static partial class ForegroundHandoff
{
    /// <summary>ASFW_ANY: any process may take the foreground until her next input.</summary>
    private const uint AnyProcess = uint.MaxValue;

    public static bool TryAllowPipeServer(NamedPipeClientStream client)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            if (TryGetServerProcessId(client) is { } processId)
            {
                return AllowSetForegroundWindow(processId);
            }

            // The pipe could not name the primary; this short-lived process is
            // about to exit, so a grant to whichever process asks first is
            // still bounded by her next keystroke or click.
            return AllowSetForegroundWindow(AnyProcess);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The primary's process id behind a connected activation pipe, or
    /// null when Windows cannot say.</summary>
    internal static uint? TryGetServerProcessId(NamedPipeClientStream client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId) && processId != 0
            ? processId
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
