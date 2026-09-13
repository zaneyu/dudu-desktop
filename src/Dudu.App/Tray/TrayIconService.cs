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
    TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayCommand> commands) => null;
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
    private Func<Action, Task>? _ownerDispatcher;
    private nint _ownerWindow;
    private int _ownerThreadId;
    private bool _created;
    private bool _disposed;

    public TrayIconService(
        ITrayNativeApi? native = null,
        Action<TrayCommand>? commandHandler = null,
        string tooltip = "dudu")
    {
        _native = native ?? new WindowsTrayNativeApi();
        _commandHandler = commandHandler
            ?? throw new ArgumentNullException(nameof(commandHandler));
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

    public void Attach(nint ownerWindow, Func<Action, Task>? ownerDispatcher = null)
    {
        if (ownerWindow == 0) throw new ArgumentOutOfRangeException(nameof(ownerWindow));
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureOwnerThread();
            _ownerDispatcher = ownerDispatcher;
            if (_created && _ownerWindow == ownerWindow) return;
            if (_created) _native.Remove(_ownerWindow);
            _ownerWindow = ownerWindow;
            _created = _native.Add(ownerWindow, CallbackMessage, _tooltip);
            if (!_created) throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");
        }
    }

    public bool HandleWindowMessage(uint message, nint lParam) =>
        HandleWindowMessage(message, 0, lParam);

    public bool HandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        TrayCommand? command = null;
        var recreate = false;
        nint ownerWindow = 0;
        IReadOnlyList<TrayCommand>? commands = null;
        lock (_gate)
        {
            if (_disposed) return false;
            if (message == TaskbarCreatedFallbackMessage || message == TaskbarCreatedMessage)
            {
                recreate = true;
            }
            else if (message == 0x0111 /* WM_COMMAND */)
            {
                var commandIndex = (int)(unchecked((nuint)wParam) & 0xffff);
                if (commandIndex is > 0 and <= 7)
                {
                    command = Commands[commandIndex - 1];
                }
                else return false;
            }
            else if (message != CallbackMessage) return false;
            if (message == CallbackMessage
                && unchecked((uint)lParam) == 0x0205 /* WM_RBUTTONUP */)
            {
                ownerWindow = _ownerWindow;
                commands = Commands.ToArray();
            }
            else if (message == CallbackMessage) return false;
        }

        if (recreate)
        {
            Recreate();
            return true;
        }

        if (commands is not null)
        {
            // TrackPopupMenuEx runs a nested native message loop. Never hold
            // the service lock while Windows or the command callback runs.
            command = _native.TrackPopupMenu(ownerWindow, commands);
        }

        if (command is { } selected)
        {
            ExecuteCommand(selected);
        }

        return true;
    }

    public void ExecuteCommand(TrayCommand command)
    {
        Action<TrayCommand> handler;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Commands.Contains(command)) throw new ArgumentOutOfRangeException(nameof(command));
            handler = _commandHandler;
        }

        handler(command);
    }

    public void Recreate()
    {
        if (NeedsOwnerDispatch())
        {
            _ownerDispatcher!(() => Recreate()).GetAwaiter().GetResult();
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            RecreateOnOwnerThread();
        }
    }

    public void Dispose()
    {
        if (NeedsOwnerDispatch())
        {
            _ownerDispatcher!(() => Dispose()).GetAwaiter().GetResult();
            return;
        }

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

    private bool NeedsOwnerDispatch()
    {
        lock (_gate)
        {
            return _ownerThreadId != 0
                && _ownerThreadId != Environment.CurrentManagedThreadId
                && _ownerDispatcher is not null;
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

    public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayCommand> commands) =>
        NativeTrayMenu.Show(ownerWindow, commands);
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
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE
                | NOTIFY_ICON_DATA_FLAGS.NIF_TIP
                | NOTIFY_ICON_DATA_FLAGS.NIF_ICON,
            uCallbackMessage = callbackMessage,
            szTip = tooltip,
            hIcon = PInvoke.LoadIcon(HINSTANCE.Null, new PCWSTR((char*)32512)),
        };
        return PInvoke.Shell_NotifyIcon(
            add ? NOTIFY_ICON_MESSAGE.NIM_ADD : NOTIFY_ICON_MESSAGE.NIM_DELETE,
            data);
    }

    public static bool Remove(nint ownerWindow) => Change(ownerWindow, 0, string.Empty, add: false);
}

internal static unsafe class NativeTrayMenu
{
    private const uint MfString = 0x0000;
    private const uint TpmRightButton = 0x0002;

    public static TrayCommand? Show(nint ownerWindow, IReadOnlyList<TrayCommand> commands)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Tray menus require Windows.");
        }

        using var menu = PInvoke.CreatePopupMenu_SafeHandle();
        if (menu.IsInvalid) return null;

        for (var index = 0; index < commands.Count; index++)
        {
            var label = GetLabel(commands[index]);
            if (!PInvoke.AppendMenu(
                    menu,
                    (MENU_ITEM_FLAGS)MfString,
                    (nuint)(index + 1),
                    label))
            {
                return null;
            }
        }

        if (!PInvoke.GetCursorPos(out var point)) return null;
        _ = PInvoke.TrackPopupMenuEx(
            menu,
            TpmRightButton,
            point.X,
            point.Y,
            new HWND((void*)ownerWindow),
            null);
        return null;
    }

    private static string GetLabel(TrayCommand command) => command switch
    {
        TrayCommand.ShowOrHide => "show or hide dudu",
        TrayCommand.PauseOneHour => "pause for one hour",
        TrayCommand.PauseUntilTomorrowAtSeven => "pause until tomorrow at 07:00",
        TrayCommand.PauseUntilFullscreenEnds => "pause until fullscreen ends",
        TrayCommand.PauseIndefinitelyOrResume => "pause indefinitely or resume",
        TrayCommand.OpenSettings => "open settings",
        TrayCommand.Exit => "exit",
        _ => command.ToString(),
    };
}
