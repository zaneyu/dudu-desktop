using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.Tray;

public enum TrayCommand
{
    ShowOrHide,
    PauseOneHour,
    PauseUntilTomorrowAtSeven,
    PauseUntilFullscreenEnds,
    PauseIndefinitelyOrResume,
    OpenSettings,
    Exit,
}

public interface ITrayNativeApi
{
    bool Add(nint ownerWindow, uint callbackMessage, string tooltip);
    bool Remove(nint ownerWindow);
    bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip);
}

public sealed class TrayIconService : IDisposable
{
    public const uint CallbackMessage = PInvoke.WM_APP + 20;
    public const uint TaskbarCreatedFallbackMessage = 0x8001;

    public static uint TaskbarCreatedMessage => OperatingSystem.IsWindows()
        ? PInvoke.RegisterWindowMessage("TaskbarCreated")
        : TaskbarCreatedFallbackMessage;

    private readonly ITrayNativeApi _native;
    private readonly Action<TrayCommand> _commandHandler;
    private readonly object _gate = new();
    private readonly string _tooltip;
    private nint _ownerWindow;
    private int _ownerThreadId;
    private bool _created;
    private bool _disposed;

    public TrayIconService(
        ITrayNativeApi? native = null,
        Action<TrayCommand>? commandHandler = null,
        string tooltip = "Dudu")
    {
        _native = native ?? new WindowsTrayNativeApi();
        _commandHandler = commandHandler ?? (_ => { });
        _tooltip = tooltip;
    }

    public IReadOnlyList<TrayCommand> Commands { get; } =
    [
        TrayCommand.ShowOrHide,
        TrayCommand.PauseOneHour,
        TrayCommand.PauseUntilTomorrowAtSeven,
        TrayCommand.PauseUntilFullscreenEnds,
        TrayCommand.PauseIndefinitelyOrResume,
        TrayCommand.OpenSettings,
        TrayCommand.Exit,
    ];

    public void Attach(nint ownerWindow)
    {
        if (ownerWindow == 0) throw new ArgumentOutOfRangeException(nameof(ownerWindow));
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureOwnerThread();
            if (_created && _ownerWindow == ownerWindow) return;
            if (_created) _native.Remove(_ownerWindow);
            _ownerWindow = ownerWindow;
            _created = _native.Add(ownerWindow, CallbackMessage, _tooltip);
            if (!_created) throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");
        }
    }

    public bool HandleWindowMessage(uint message, nint lParam)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (message == TaskbarCreatedFallbackMessage || message == TaskbarCreatedMessage)
            {
                RecreateOnOwnerThread();
                return true;
            }

            if (message != CallbackMessage) return false;
            var notification = unchecked((uint)lParam);
            if (notification == 0x0205 /* WM_RBUTTONUP */)
            {
                // The application owns menu presentation; this service only
                // translates menu choices so it never activates the pet HWND.
                return true;
            }

            return false;
        }
    }

    public void ExecuteCommand(TrayCommand command)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Commands.Contains(command)) throw new ArgumentOutOfRangeException(nameof(command));
            _commandHandler(command);
        }
    }

    public void Recreate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RecreateOnOwnerThread();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            EnsureOwnerThread();
            if (_created)
            {
                _native.Remove(_ownerWindow);
                _created = false;
            }

            _disposed = true;
        }
    }

    private void RecreateOnOwnerThread()
    {
        EnsureOwnerThread();
        if (_ownerWindow == 0) return;
        _created = _native.Recreate(_ownerWindow, CallbackMessage, _tooltip);
    }

    private void EnsureOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("Tray icon operations must run on its owner thread.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TrayIconService));
    }
}

internal sealed class WindowsTrayNativeApi : ITrayNativeApi
{
    public bool Add(nint ownerWindow, uint callbackMessage, string tooltip) =>
        NativeShellNotifyIcon.Change(ownerWindow, callbackMessage, tooltip, add: true);

    public bool Remove(nint ownerWindow) =>
        NativeShellNotifyIcon.Remove(ownerWindow);

    public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip)
    {
        NativeShellNotifyIcon.Remove(ownerWindow);
        return NativeShellNotifyIcon.Change(ownerWindow, callbackMessage, tooltip, add: true);
    }
}

internal static unsafe class NativeShellNotifyIcon
{
    public static bool Change(nint ownerWindow, uint callbackMessage, string tooltip, bool add)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The tray requires Windows.");
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = new HWND((void*)ownerWindow),
            uID = 1,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = callbackMessage,
            szTip = tooltip,
        };
        return PInvoke.Shell_NotifyIcon(
            add ? NOTIFY_ICON_MESSAGE.NIM_ADD : NOTIFY_ICON_MESSAGE.NIM_DELETE,
            data);
    }

    public static bool Remove(nint ownerWindow) => Change(ownerWindow, 0, string.Empty, add: false);
}
