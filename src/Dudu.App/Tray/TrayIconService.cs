using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.App.System;
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

/// <summary>
/// What the tray menu needs to know to describe the current state instead of
/// offering ambiguous "show or hide" / "pause indefinitely or resume" items.
/// </summary>
public sealed record TrayMenuState(bool PetVisible, PauseMode PauseMode);

/// <summary>One tray menu row: its command, the label shown for it right
/// now, and whether it carries a check mark (the active pause).</summary>
public sealed record TrayMenuItem(TrayCommand Command, string Label, bool Checked = false);

public interface ITrayNativeApi
{
    bool Add(nint ownerWindow, uint callbackMessage, string tooltip);
    bool Remove(nint ownerWindow);
    bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip);
    TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayCommand> commands) => null;

    TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayMenuItem> items) =>
        TrackPopupMenu(ownerWindow, items.Select(item => item.Command).ToArray());
}

public sealed class TrayIconService : IDisposable, IAsyncDisposable
{
    public const uint CallbackMessage = PInvoke.WM_APP + 20;
    public const uint TaskbarCreatedFallbackMessage = 0x8001;
    public static readonly TimeSpan OwnerDispatchTimeout = TimeSpan.FromSeconds(5);

    public static uint TaskbarCreatedMessage => OperatingSystem.IsWindows()
        ? PInvoke.RegisterWindowMessage("TaskbarCreated")
        : TaskbarCreatedFallbackMessage;

    private readonly ITrayNativeApi _native;
    private readonly Action<TrayCommand> _commandHandler;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly object _gate = new();
    private readonly string _tooltip;
    private readonly Func<TrayMenuState>? _menuState;
    private Func<Action, Task>? _ownerDispatcher;
    private nint _ownerWindow;
    private int _ownerThreadId;
    private bool _created;
    private bool _disposed;

    public TrayIconService(
        ITrayNativeApi? native = null,
        Action<TrayCommand>? commandHandler = null,
        string tooltip = "dudu",
        IAppHostErrorReporter? errorReporter = null,
        Func<TrayMenuState>? menuState = null)
    {
        _native = native ?? new WindowsTrayNativeApi();
        _commandHandler = commandHandler
            ?? throw new ArgumentNullException(nameof(commandHandler));
        _tooltip = tooltip;
        _errorReporter = errorReporter;
        _menuState = menuState;
    }

    /// <summary>
    /// The rows the popup menu shows right now. With a state provider the
    /// two toggles say what they will actually do ("hide dudu" vs "show
    /// dudu", "resume dudu" while any pause is active) and the active pause
    /// is checked; without one (or if reading it fails) the neutral
    /// either-way labels are kept.
    /// </summary>
    public IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        TrayMenuState? state = null;
        if (_menuState is not null)
        {
            try
            {
                state = _menuState();
            }
            catch (Exception exception)
            {
                ReportFailure("tray-menu-state", exception);
            }
        }

        return Commands
            .Select(command => new TrayMenuItem(
                command,
                TrayMenuLabels.For(command, state),
                state is not null && TrayMenuLabels.IsChecked(command, state)))
            .ToArray();
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
            if (!_created)
            {
                // Logging only: the throw behavior is unchanged, but a failed
                // native attach now leaves an operation-named diagnostic.
                ReportFailure(
                    "tray-attach",
                    new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed."));
                throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");
            }
        }
    }

    public bool HandleWindowMessage(uint message, nint lParam) =>
        HandleWindowMessage(message, 0, lParam);

    public bool HandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        TrayCommand? command = null;
        var recreate = false;
        var showMenu = false;
        nint ownerWindow = 0;
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
                if (commandIndex > 0 && commandIndex <= Commands.Count)
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
                showMenu = true;
            }
            else if (message == CallbackMessage
                && unchecked((uint)lParam) == 0x0202 /* WM_LBUTTONUP */)
            {
                // A plain click on the tray icon used to do nothing at all;
                // the only way in was the right-click menu. Open Dudu's
                // window, the one thing a click on an app's tray icon is
                // expected to do.
                command = TrayCommand.OpenSettings;
            }
            else if (message == CallbackMessage) return false;
        }

        if (recreate)
        {
            try
            {
                Recreate();
            }
            catch (Exception exception)
            {
                // Logging only: the failure still propagates to the window
                // procedure exactly as before, now with an operation name plus
                // the exception type attached.
                ReportFailure("tray-recreate", exception);
                throw;
            }
            return true;
        }

        if (showMenu)
        {
            // TrackPopupMenuEx runs a nested native message loop. Never hold
            // the service lock while Windows or the command callback runs
            // (BuildMenu's state callback included).
            command = _native.TrackPopupMenu(ownerWindow, BuildMenu());
        }

        if (command is { } selected)
        {
            ExecuteCommand(selected);
        }

        return true;
    }

    /// <summary>
    /// Shows the same menu a right-click on the tray icon shows, for other
    /// surfaces that should offer it (a right-click on the pet itself).
    /// Must run on the owner thread, like the tray callback it mirrors.
    /// Returns false when the tray is not attached or already disposed.
    /// </summary>
    public bool ShowMenu()
    {
        nint ownerWindow;
        lock (_gate)
        {
            if (_disposed || _ownerWindow == 0) return false;
            ownerWindow = _ownerWindow;
        }

        if (_native.TrackPopupMenu(ownerWindow, BuildMenu()) is { } selected)
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
            throw new InvalidOperationException(
                "Tray Recreate must run on its owner thread; use RecreateAsync from other threads.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            RecreateOnOwnerThread();
        }
    }

    public async Task RecreateAsync(CancellationToken cancellationToken = default)
    {
        Func<Action, Task>? dispatcher;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!NeedsOwnerDispatch())
            {
                RecreateOnOwnerThread();
                return;
            }

            dispatcher = _ownerDispatcher;
        }

        await dispatcher!(() => Recreate())
            .WaitAsync(OwnerDispatchTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (NeedsOwnerDispatch())
        {
            throw new InvalidOperationException(
                "Tray Dispose must run on its owner thread; use DisposeAsync from other threads.");
        }

        DisposeOnOwnerThread();
    }

    public async ValueTask DisposeAsync()
    {
        Func<Action, Task>? dispatcher;
        lock (_gate)
        {
            if (_disposed) return;
            if (!NeedsOwnerDispatch())
            {
                DisposeOnOwnerThread();
                return;
            }

            dispatcher = _ownerDispatcher;
        }

        await dispatcher!(() => Dispose())
            .WaitAsync(OwnerDispatchTimeout)
            .ConfigureAwait(false);
    }

    private void DisposeOnOwnerThread()
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
        if (!_created)
        {
            // Best-effort recreate stays best-effort: no throw, but a failed
            // native recreate (e.g. explorer restarted into a broken state)
            // now leaves an operation-named diagnostic instead of silence.
            ReportFailure(
                "tray-recreate",
                new InvalidOperationException("Shell_NotifyIcon tray recreate reported failure."));
        }
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

    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu tray operation '{0}' failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
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
        NativeTrayMenu.Show(
            ownerWindow,
            commands.Select(command => new TrayMenuItem(command, TrayMenuLabels.For(command, null))).ToArray());

    public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayMenuItem> items) =>
        NativeTrayMenu.Show(ownerWindow, items);
}

/// <summary>Tray menu copy, kept apart from the native menu so it can be
/// tested without Windows.</summary>
public static class TrayMenuLabels
{
    public static string For(TrayCommand command, TrayMenuState? state) => command switch
    {
        TrayCommand.ShowOrHide => state is null
            ? "show or hide dudu"
            : state.PetVisible ? "hide dudu" : "show dudu",
        TrayCommand.PauseOneHour => "pause for one hour",
        TrayCommand.PauseUntilTomorrowAtSeven => "pause until tomorrow at 07:00",
        TrayCommand.PauseUntilFullscreenEnds => "pause until fullscreen ends",
        TrayCommand.PauseIndefinitelyOrResume => state is null
            ? "pause indefinitely or resume"
            : state.PauseMode == PauseMode.None ? "pause until i resume" : "resume dudu",
        TrayCommand.OpenSettings => "open settings",
        TrayCommand.Exit => "exit",
        _ => command.ToString(),
    };

    /// <summary>Checks the pause row matching the active pause, so the menu
    /// shows that (and how) Dudu is paused.</summary>
    public static bool IsChecked(TrayCommand command, TrayMenuState state) => command switch
    {
        TrayCommand.PauseOneHour => state.PauseMode == PauseMode.OneHour,
        TrayCommand.PauseUntilTomorrowAtSeven => state.PauseMode == PauseMode.UntilTomorrowAtSeven,
        TrayCommand.PauseUntilFullscreenEnds => state.PauseMode == PauseMode.UntilFullscreenEnds,
        _ => false,
    };
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
            hIcon = LoadTrayIcon(),
        };
        return PInvoke.Shell_NotifyIcon(
            add ? NOTIFY_ICON_MESSAGE.NIM_ADD : NOTIFY_ICON_MESSAGE.NIM_DELETE,
            data);
    }

    public static bool Remove(nint ownerWindow) => Change(ownerWindow, 0, string.Empty, add: false);

    /// <summary>
    /// The tray used to show the stock Windows application icon, so Dudu
    /// could not be told apart from any other program in the notification
    /// area. The SDK embeds ApplicationIcon (dudu.ico) as the executable's
    /// icon group 32512; fall back to the stock icon only if it is missing.
    /// </summary>
    private static HICON LoadTrayIcon()
    {
        var idiApplication = new PCWSTR((char*)32512);
        using var module = PInvoke.GetModuleHandle((string?)null);
        HICON icon = default;
        if (!module.IsInvalid)
        {
            icon = PInvoke.LoadIcon(new HINSTANCE(module.DangerousGetHandle()), idiApplication);
        }

        return icon.IsNull ? PInvoke.LoadIcon(HINSTANCE.Null, idiApplication) : icon;
    }
}

internal static unsafe class NativeTrayMenu
{
    private const uint MfString = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint TpmRightButton = 0x0002;
    private const uint WmNull = 0x0000;

    public static TrayCommand? Show(nint ownerWindow, IReadOnlyList<TrayMenuItem> items)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Tray menus require Windows.");
        }

        using var menu = PInvoke.CreatePopupMenu_SafeHandle();
        if (menu.IsInvalid) return null;

        // WM_COMMAND ids are the item's index in TrayIconService.Commands
        // (+1), which is what HandleWindowMessage maps back.
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!PInvoke.AppendMenu(
                    menu,
                    (MENU_ITEM_FLAGS)(MfString | (item.Checked ? MfChecked : 0)),
                    (nuint)(index + 1),
                    item.Label))
            {
                return null;
            }
        }

        if (!PInvoke.GetCursorPos(out var point)) return null;
        var owner = new HWND((void*)ownerWindow);
        // Documented TrackPopupMenu requirement for notification-icon menus:
        // the owner must be the foreground window first, or the menu never
        // closes when she clicks anywhere else and just hangs on screen. The
        // WM_NULL afterwards lets the menu dismiss cleanly on the next click.
        _ = PInvoke.SetForegroundWindow(owner);
        _ = PInvoke.TrackPopupMenuEx(
            menu,
            TpmRightButton,
            point.X,
            point.Y,
            owner,
            null);
        _ = PInvoke.PostMessage(owner, WmNull, 0, 0);
        return null;
    }
}
