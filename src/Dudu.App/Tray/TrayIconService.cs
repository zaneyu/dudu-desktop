using System.Diagnostics;
using System.Runtime.InteropServices;
using Dudu.App.Hosting;
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

    /// <summary>Shows the menu with the given per-command labels (parallel to
    /// <paramref name="commands"/>), so a label can reflect current state --
    /// e.g. "resume dudu" while a pause is active.</summary>
    TrayCommand? TrackPopupMenu(
        nint ownerWindow,
        IReadOnlyList<TrayCommand> commands,
        IReadOnlyList<string> labels) => TrackPopupMenu(ownerWindow, commands);
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
    private readonly Func<TrayCommand, string?>? _labelOverride;
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
        Func<TrayCommand, string?>? labelOverride = null)
    {
        _native = native ?? new WindowsTrayNativeApi();
        _commandHandler = commandHandler
            ?? throw new ArgumentNullException(nameof(commandHandler));
        _tooltip = tooltip;
        _errorReporter = errorReporter;
        _labelOverride = labelOverride;
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

    /// <summary>The menu label for a command when no state-dependent override applies.</summary>
    public static string DefaultLabel(TrayCommand command) => command switch
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

    /// <summary>The labels the menu is about to show: <see cref="DefaultLabel"/>
    /// unless the composed label override returns something else for the
    /// current state. A faulting override falls back to the default label.</summary>
    public IReadOnlyList<string> ResolveLabels(IReadOnlyList<TrayCommand> commands)
    {
        var labels = new string[commands.Count];
        for (var index = 0; index < commands.Count; index++)
        {
            string? label = null;
            if (_labelOverride is not null)
            {
                try { label = _labelOverride(commands[index]); }
                catch (Exception exception) { ReportFailure("tray-label", exception); }
            }

            labels[index] = string.IsNullOrWhiteSpace(label) ? DefaultLabel(commands[index]) : label;
        }

        return labels;
    }

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
                commands = Commands.ToArray();
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

        if (commands is not null)
        {
            // TrackPopupMenuEx runs a nested native message loop. Never hold
            // the service lock while Windows or the command callback runs.
            command = _native.TrackPopupMenu(ownerWindow, commands, ResolveLabels(commands));
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
        NativeTrayMenu.Show(ownerWindow, commands, commands.Select(TrayIconService.DefaultLabel).ToArray());

    public TrayCommand? TrackPopupMenu(
        nint ownerWindow,
        IReadOnlyList<TrayCommand> commands,
        IReadOnlyList<string> labels) =>
        NativeTrayMenu.Show(ownerWindow, commands, labels);
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
            hIcon = new HICON((void*)TrayAppIcon.Handle),
        };
        return PInvoke.Shell_NotifyIcon(
            add ? NOTIFY_ICON_MESSAGE.NIM_ADD : NOTIFY_ICON_MESSAGE.NIM_DELETE,
            data);
    }

    public static bool Remove(nint ownerWindow) => Change(ownerWindow, 0, string.Empty, add: false);
}

/// <summary>
/// The notification-area icon: Dudu's own icon (the csproj
/// <c>ApplicationIcon</c>, embedded in the executable by the SDK), not the
/// generic Windows application icon. Loaded once at the small-icon metric
/// and kept for the process lifetime -- Shell_NotifyIcon copies the icon, and
/// every recreate reuses the same handle instead of leaking a fresh one.
/// </summary>
internal static unsafe class TrayAppIcon
{
    /// <summary>The icon-group id the .NET SDK (via Roslyn's default Win32
    /// resources) gives the ApplicationIcon in the managed assembly and the
    /// apphost copies into the executable.</summary>
    internal const int ApplicationIconResourceId = 32512;
    private const uint ImageIcon = 1;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private static nint _handle;

    public static nint Handle
    {
        get
        {
            var cached = Volatile.Read(ref _handle);
            if (cached != 0) return cached;
            var loaded = Load();
            var previous = Interlocked.CompareExchange(ref _handle, loaded, 0);
            return previous != 0 ? previous : loaded;
        }
    }

    private static nint Load()
    {
        try
        {
            var width = GetSystemMetrics(SmCxSmIcon);
            var height = GetSystemMetrics(SmCySmIcon);

            // 1. The embedded app icon from this executable's own resources.
            var module = GetModuleHandleW(null);
            if (module != 0)
            {
                var icon = LoadImageW(module, ApplicationIconResourceId, ImageIcon, width, height, 0);
                if (icon != 0) return icon;
            }

            // 2. Whatever icon group the executable carries first, whatever
            //    its resource id -- the same icon Explorer shows for the exe.
            if (Environment.ProcessPath is { Length: > 0 } processPath)
            {
                nint small = 0;
                if (ExtractIconExW(processPath, 0, null, &small, 1) > 0 && small != 0)
                {
                    return small;
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        // 3. Last resort: the generic system application icon (IDI_APPLICATION).
        return (nint)PInvoke.LoadIcon(HINSTANCE.Null, new PCWSTR((char*)32512)).Value;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint instance, nint name, uint type, int width, int height, uint load);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconExW(string file, int iconIndex, nint* large, nint* small, uint icons);
}

internal static unsafe class NativeTrayMenu
{
    private const uint MfString = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint WmNull = 0x0000;

    public static TrayCommand? Show(
        nint ownerWindow,
        IReadOnlyList<TrayCommand> commands,
        IReadOnlyList<string> labels)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Tray menus require Windows.");
        }

        using var menu = PInvoke.CreatePopupMenu_SafeHandle();
        if (menu.IsInvalid) return null;

        for (var index = 0; index < commands.Count; index++)
        {
            var label = index < labels.Count && !string.IsNullOrWhiteSpace(labels[index])
                ? labels[index]
                : TrayIconService.DefaultLabel(commands[index]);
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
        var owner = new HWND((void*)ownerWindow);
        // KB135788: a notification-area menu only dismisses when she clicks
        // elsewhere (and only takes keyboard focus for arrow/Enter/Esc) if its
        // owner is the foreground window while TrackPopupMenuEx runs, and the
        // owner must receive a message right after it returns so the next
        // right-click opens the menu instead of silently closing it.
        _ = PInvoke.SetForegroundWindow(owner);
        _ = PInvoke.TrackPopupMenuEx(
            menu,
            TpmRightButton,
            point.X,
            point.Y,
            owner,
            null);
        _ = PInvoke.PostMessage(owner, WmNull, default, default);
        return null;
    }
}
