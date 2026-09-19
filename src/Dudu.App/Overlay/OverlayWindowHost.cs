using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.App.Hosting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.Overlay;

/// <summary>
/// Owns one no-activate popup window and its message thread.
/// </summary>
public sealed unsafe class OverlayWindowHost : IFramePresenter, IDisposable, IAsyncDisposable
{
    internal const uint HostCommandMessage = PInvoke.WM_APP + 1;
    internal const uint ShutdownCommandMessage = PInvoke.WM_APP + 2;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmDestroy = 0x0002;
    private const uint WmCancelMode = 0x001F;
    private const uint WmCaptureChanged = 0x0215;
    private const nint MA_NOACTIVATE = 3;
    private const nint HTCLIENT = 1;
    private const nint HTTRANSPARENT = -1;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Extended style for the pet window. Deliberately omits WS_EX_TRANSPARENT:
    /// that flag makes a window click-through for *all* input, which would
    /// defeat the per-pixel hit testing this host implements itself in
    /// WM_NCHITTEST (see <see cref="IsInteractive(int, int)"/>). Per-pixel
    /// click-through already comes from returning HTTRANSPARENT for
    /// fully-transparent pixels there.
    /// </summary>
    internal const WINDOW_EX_STYLE PetWindowExStyle =
        WINDOW_EX_STYLE.WS_EX_LAYERED
        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    /// <summary>
    /// Window class style for the pet window. CS_DBLCLKS is required for
    /// WM_LBUTTONDBLCLK (double-click to open Home) to ever be delivered.
    /// </summary>
    internal const WNDCLASS_STYLES PetWindowClassStyle = WNDCLASS_STYLES.CS_DBLCLKS;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly object ClassGate = new();
    public const string WindowClassName = "Dudu.DesktopCompanion.PetOverlay.v1";
    private static readonly string ClassName = WindowClassName;
    private static readonly ConcurrentDictionary<nint, OverlayWindowHost> Hosts = new();
    private static readonly HWND TopmostWindow = new((void*)(-1));
    private static readonly HWND NotTopmostWindow = new((void*)(-2));
    private static readonly WNDPROC WindowProcedure = WindowProc;
    private static readonly MONITORENUMPROC MonitorProcedure = MonitorCallback;

    private readonly IFramePresenter _presenter;
    private readonly PixelSize _nominalSize;
    private readonly Func<CancellationToken, Task> _openHome;
    private readonly Func<PetPlacement, CancellationToken, Task>? _persistPlacementAsync;
    private readonly Action _showContextMenu;
    private readonly Action<uint, nint, nint>? _systemMessageHandler;
    private readonly Action<Exception> _diagnostic;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly IReadOnlyList<PixelRect> _bubbleHitRegions;
    private OverlayActionSurfaceController? _actionSurface;
    private bool _alwaysOnTop = true;
    private readonly OwnerActionQueue _ownerActions;
    private readonly TaskCompletionSource<OverlayWindowHost> _created =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly CancellationToken _creationCancellation;
    private readonly OverlayOwnerMessageRouter _ownerMessageRouter;
    private readonly OverlayActionDispatchQueue _actionDispatchQueue;
    private CancellationTokenRegistration _creationRegistration;
    private PetPlacement _placement;
    private PixelRect _windowBounds;
    private HWND _window;
    private bool _dragging;
    private bool _actionSurfacePointerArmed;
    private OverlaySurfaceAction? _armedOverlayAction;
    private bool _petBodyPointerArmed;
    private bool _placementDirty;
    private int _dragOriginX;
    private int _dragOriginY;
    private int _dragOriginScreenX;
    private int _dragOriginScreenY;
    private PixelRect _dragStartBounds;
    private bool _shutdownIssued;
    private int _shutdownRequestPosted;
    private int _ownerThreadId;
    private int _currentDpi = 96;

    private OverlayWindowHost(
        IFramePresenter presenter,
        PetPlacement placement,
        PixelSize nominalSize,
        Func<CancellationToken, Task>? openHome,
        Action? showContextMenu,
        IReadOnlyList<PixelRect>? bubbleHitRegions,
        Action<Exception>? diagnostic,
        Action<uint, nint, nint>? systemMessageHandler,
        CancellationToken creationCancellation,
        IAppHostErrorReporter? errorReporter = null,
        Func<PetPlacement, CancellationToken, Task>? persistPlacementAsync = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        if (nominalSize.Width <= 0 || nominalSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize));
        }

        _nominalSize = nominalSize;
        _openHome = openHome ?? (_ => Task.CompletedTask);
        _persistPlacementAsync = persistPlacementAsync;
        _showContextMenu = showContextMenu ?? (() => { });
        _systemMessageHandler = systemMessageHandler;
        _bubbleHitRegions = bubbleHitRegions?.ToArray() ?? [];
        _diagnostic = diagnostic ?? DefaultDiagnostic;
        _errorReporter = errorReporter;
        _actionDispatchQueue = new OverlayActionDispatchQueue(_diagnostic);
        _creationCancellation = creationCancellation;
        _ownerActions = new OwnerActionQueue(
            () => _ownerThreadId == Environment.CurrentManagedThreadId,
            PostOwnerSignal,
            ExecuteOwnerAction);
        _ownerMessageRouter = new OverlayOwnerMessageRouter(
            HostCommandMessage,
            ShutdownCommandMessage,
            DrainOwnerActions,
            ShutdownOnOwnerThread);
        _ownerThread = new Thread(OwnerThreadMain)
        {
            IsBackground = true,
            Name = "Dudu overlay window owner",
        };
    }

    public static Task<OverlayWindowHost> CreateAsync(
        IFramePresenter presenter,
        PetPlacement placement,
        PixelSize nominalSize,
        Func<CancellationToken, Task>? openHome = null,
        Action? showContextMenu = null,
        IReadOnlyList<PixelRect>? bubbleHitRegions = null,
        Action<Exception>? diagnostic = null,
        Action<uint, nint, nint>? systemMessageHandler = null,
        CancellationToken cancellationToken = default,
        IAppHostErrorReporter? errorReporter = null,
        Func<PetPlacement, CancellationToken, Task>? persistPlacementAsync = null)
    {
        var host = new OverlayWindowHost(
            presenter,
            placement,
            nominalSize,
            openHome,
            showContextMenu,
            bubbleHitRegions,
            diagnostic,
            systemMessageHandler,
            cancellationToken,
            errorReporter,
            persistPlacementAsync);
        host._creationRegistration = cancellationToken.Register(
            static state => ((OverlayWindowHost)state!).CancelStartup(),
            host);
        host._ownerThread.Start();
        return OverlayWindowHostLifecycle.WaitForCreationAsync(host, cancellationToken);
    }

    public nint Handle => (nint)_window.Value;

    // Desired visibility is written synchronously by Show/Hide so cross-thread
    // readers (lifecycle adapter, coordinator) never observe a stale value
    // while the owner action is still queued. The owner action applies the
    // latest desired state, so out-of-order show/hide posts converge.
    private volatile bool _desiredVisible;

    public bool IsVisible => _desiredVisible;

    internal Task<OverlayWindowHost> CreationTask => _created.Task;

    internal void CancelCreation() => CancelStartup();

    internal void JoinAfterCreationFailure() => StopAndJoin();

    public static nint GetForegroundWindowHandle() =>
        (nint)PInvoke.GetForegroundWindow().Value;

    public static nint GetFocusHandle() =>
        (nint)PInvoke.GetFocus().Value;

    public static bool IsDuduWindowHandle(nint hwnd) =>
        hwnd != 0 && Hosts.ContainsKey(hwnd);

    public static bool TrySetForegroundWindow(nint hwnd) =>
        hwnd != 0 && PInvoke.SetForegroundWindow(new HWND((void*)hwnd));

    public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _presenter.PresentAsync(frame, cancellationToken);
    }

    public void Show()
    {
        _desiredVisible = true;
        PostToOwner(ApplyDesiredVisibility);
    }

    public void Hide()
    {
        _desiredVisible = false;
        PostToOwner(ApplyDesiredVisibility);
    }

    private void ApplyDesiredVisibility()
    {
        if (_desiredVisible)
        {
            _ = PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }
        else
        {
            _ = PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_HIDE);
            CommitPlacementIfDirty();
            ReleasePointerCapture();
        }
    }

    public void SetPlacement(PetPlacement placement)
    {
        _ = SetPlacementAsync(placement);
    }

    public Task SetPlacementAsync(
        PetPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();
        return InvokeOnOwnerAsync(() =>
        {
            _placement = placement;
            ResolveAndMove();
        }, cancellationToken);
    }

    /// <summary>Installs the shared action contract used by native no-activate
    /// hit testing and layered-frame rendering. The separate Settings controls
    /// remain the keyboard and UIA route for the same actions.</summary>
    public Task SetActionSurfaceAsync(
        OverlayActionSurfaceController actionSurface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionSurface);
        return InvokeOnOwnerAsync(() => _actionSurface = actionSurface, cancellationToken);
    }

    public Task SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        InvokeOnOwnerAsync(() =>
        {
            _alwaysOnTop = alwaysOnTop;
            SetNativeWindowState(_windowBounds);
        }, cancellationToken);

    public void RestorePlacement() => PostToOwner(ResolveAndMove);

    public Task<MonitorPlacementSnapshot> CapturePlacementSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InvokeOnOwnerAsync(() =>
        {
            var monitors = EnumerateMonitors();
            return MonitorPlacementService.CaptureSnapshot(
                _windowBounds,
                _placement.Scale,
                _nominalSize,
                monitors);
        }, cancellationToken);
    }

    public Task InvokeOnOwnerAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _ownerActions.InvokeAsync(action, cancellationToken);
    }

    public Task<TResult> InvokeOnOwnerAsync<TResult>(
        Func<TResult> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _ownerActions.InvokeAsync(action, cancellationToken);
    }

    public void Dispose()
    {
        CancelStartup();
        StopAndJoin();
        WaitForActionDispatchCompletion();
    }

    public ValueTask DisposeAsync()
    {
        CancelStartup();
        if (_ownerThreadId == Environment.CurrentManagedThreadId)
        {
            ShutdownOnOwnerThread();
            return OverlayWindowHostLifecycle.WaitForActionDispatchAsync(this);
        }

        return OverlayWindowHostLifecycle.WaitForStopAndActionDispatchAsync(this);
    }

    internal bool OwnerThreadIsAlive => _ownerThread.IsAlive;

    internal Task StoppedTask => _stopped.Task;

    internal Task ActionDispatchCompletion => _actionDispatchQueue.Completion;

    internal TimeSpan ShutdownBudget => ShutdownTimeout;

    internal void ReportDisposalTimeout(Exception exception) =>
        ReportFailure("overlay-dispose", exception);

    private void CancelStartup()
    {
        _actionDispatchQueue.Dispose();
        _ownerActions.Close(new ObjectDisposedException(nameof(OverlayWindowHost)));

        try
        {
            _startupCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owner thread has already completed teardown.
        }
        if (!_window.IsNull && _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            PostShutdownMessage();
        }
    }

    private void StopAndJoin()
    {
        if (_ownerThreadId == Environment.CurrentManagedThreadId)
        {
            ShutdownOnOwnerThread();
            return;
        }

        if (!_window.IsNull)
        {
            PostShutdownMessage();
        }

        if (_ownerThread.IsAlive)
        {
            if (!_ownerThread.Join(ShutdownTimeout))
            {
                ReportFailure("overlay-dispose", new TimeoutException(
                    "Overlay owner thread did not stop before disposal timed out."));
            }
        }
    }

    private void PostShutdownMessage()
    {
        if (_window.IsNull)
        {
            return;
        }

        if (Interlocked.Exchange(ref _shutdownRequestPosted, 1) != 0)
        {
            return;
        }

        if (!PInvoke.PostMessage(_window, ShutdownCommandMessage, 0, 0))
        {
            Volatile.Write(ref _shutdownRequestPosted, 0);
            ReportFailure("overlay-dispose", LastWin32Error("PostMessage(shutdown)"));
        }
    }

    private void OwnerThreadMain()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            _creationCancellation.ThrowIfCancellationRequested();
            _startupCancellation.Token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The overlay requires Windows.");
            }

            if (!PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((void*)(-4)))
                && Marshal.GetLastWin32Error() != ErrorAccessDenied)
            {
                throw LastWin32Error("SetProcessDpiAwarenessContext");
            }
            RegisterWindowClass();
            _creationCancellation.ThrowIfCancellationRequested();
            _startupCancellation.Token.ThrowIfCancellationRequested();

            var initial = MonitorPlacementService.Resolve(
                _placement,
                _nominalSize,
                EnumerateMonitors());
            _windowBounds = initial.WindowBounds;

            lock (ClassGate)
            {
                using var module = PInvoke.GetModuleHandle(null);
                _window = PInvoke.CreateWindowEx(
                    PetWindowExStyle,
                    ClassName,
                    "Dudu",
                    WINDOW_STYLE.WS_POPUP,
                    _windowBounds.X,
                    _windowBounds.Y,
                    _windowBounds.Width,
                    _windowBounds.Height,
                    HWND.Null,
                    null,
                    module,
                    null);
            }

            if (_window.IsNull)
            {
                throw LastWin32Error("CreateWindowEx");
            }

            var windowDpi = PInvoke.GetDpiForWindow(_window);
            if (windowDpi > 0)
            {
                _currentDpi = checked((int)windowDpi);
            }

            Hosts[(nint)_window.Value] = this;
            if (_presenter is LayeredFramePresenter layeredPresenter)
            {
                layeredPresenter.Attach((nint)_window.Value);
            }
            ApplyWindowState(_windowBounds, initial.Scale);

            if (_creationCancellation.IsCancellationRequested || _startupCancellation.IsCancellationRequested)
            {
                ShutdownOnOwnerThread();
                throw new OperationCanceledException(_startupCancellation.Token);
            }

            _created.TrySetResult(this);
            _creationRegistration.Dispose();
            _creationRegistration = default;
            RunMessageLoop();
        }
        catch (OperationCanceledException exception)
        {
            _created.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            _created.TrySetException(exception);
            ReportFailure("overlay-create", exception);
            ShutdownOnOwnerThread();
        }
        finally
        {
            ReleasePointerCapture();
            if (!_window.IsNull)
            {
                Hosts.TryRemove((nint)_window.Value, out _);
                _window = HWND.Null;
            }

            _creationRegistration.Dispose();
            _startupCancellation.Dispose();
            _stopped.TrySetResult(true);
        }
    }

    private void RunMessageLoop()
    {
        MSG message;
        while (!_shutdownIssued)
        {
            var result = PInvoke.GetMessage(&message, HWND.Null, 0, 0);
            if (result == 0)
            {
                break;
            }

            if (result < 0)
            {
                // GetMessage failure ends the loop; teardown runs in OwnerThreadMain.
                ReportFailure("overlay-message-loop", LastWin32Error("GetMessage"));
                break;
            }

            _ = PInvoke.TranslateMessage(message);
            _ = PInvoke.DispatchMessage(message);
        }
    }

    private void RegisterWindowClass()
    {
        lock (ClassGate)
        {
            var instance = PInvoke.GetModuleHandle(null);
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = PetWindowClassStyle,
                lpfnWndProc = WindowProcedure,
                hInstance = new HINSTANCE(instance.DangerousGetHandle()),
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            using (instance)
            fixed (char* className = ClassName)
            {
                windowClass.lpszClassName = new PCWSTR(className);
                if (PInvoke.RegisterClassEx(windowClass) == 0
                    && Marshal.GetLastWin32Error() != 1410)
                {
                    throw LastWin32Error("RegisterClassEx");
                }
            }
        }
    }

    private void ResolveAndMove()
    {
        var result = MonitorPlacementService.Resolve(
            _placement,
            _nominalSize,
            EnumerateMonitors());
        ApplyWindowState(result.WindowBounds, result.Scale);
        _placement = MonitorPlacementService.ToPlacement(result);
    }

    private void ApplyDpiSuggestedRect(LPARAM lParam, WPARAM wParam)
    {
        if (lParam.Value == 0)
        {
            ResolveAndMove();
            return;
        }

        var suggested = *(RECT*)lParam.Value;
        var bounds = new PixelRect(
            suggested.left,
            suggested.top,
            suggested.right - suggested.left,
            suggested.bottom - suggested.top);
        if (!bounds.IsValid)
        {
            ResolveAndMove();
            return;
        }

        var dpi = (int)((long)wParam.Value & 0xffff);
        if (dpi > 0)
        {
            _currentDpi = dpi;
        }

        ApplyWindowState(bounds, _placement.Scale);
        _placement = MonitorPlacementService.Capture(
            bounds,
            _placement.Scale,
            _nominalSize,
            EnumerateMonitors());
    }

    private void ApplyWindowState(PixelRect bounds, double scale)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        var layeredPresenter = _presenter as LayeredFramePresenter;
        if (layeredPresenter is not null)
        {
            if (!new LayeredWindowState(bounds, scale).IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(scale));
            }

            lock (layeredPresenter.HostUpdateGate)
            {
                SetNativeWindowState(bounds);
                layeredPresenter.SetWindowState(bounds, scale);
            }
        }
        else
        {
            SetNativeWindowState(bounds);
        }

        _windowBounds = bounds;
        _actionSurface?.UpdateViewport(new PixelRect(0, 0, bounds.Width, bounds.Height));
    }

    private void SetNativeWindowState(PixelRect bounds)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (IsVisible)
        {
            flags |= SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        }

        if (!PInvoke.SetWindowPos(
                _window,
                _alwaysOnTop ? TopmostWindow : NotTopmostWindow,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags))
        {
            throw LastWin32Error("SetWindowPos");
        }
    }

    private IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var context = new MonitorEnumerationContext(ReportDiagnostic);
        var handle = GCHandle.Alloc(context);
        try
        {
            var data = new LPARAM(GCHandle.ToIntPtr(handle));
            if (!PInvoke.EnumDisplayMonitors(HDC.Null, null, MonitorProcedure, data))
            {
                throw LastWin32Error("EnumDisplayMonitors");
            }

            if (context.Error is not null)
            {
                throw context.Error;
            }

            if (context.Monitors.Count == 0)
            {
                throw new InvalidOperationException("EnumDisplayMonitors returned no valid monitors.");
            }

            return context.Monitors;
        }
        finally
        {
            handle.Free();
        }
    }

    private void PostToOwner(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ownerActions.Post(action, ReportOwnerPostFailure);
    }

    private void DrainOwnerActions()
    {
        _ownerActions.Drain(() => _shutdownIssued);
        if (_shutdownIssued)
        {
            _ownerActions.Close(new ObjectDisposedException(nameof(OverlayWindowHost)));
        }
    }

    private void ExecuteOwnerAction(OwnerActionQueue.QueuedOwnerAction action)
    {
        try
        {
            action.Run();
        }
        catch (Exception exception)
        {
            // Owner-drain faults are per-action diagnostics; only window
            // creation failures tear down the host.
            ReportDiagnostic(exception);
        }
    }

    private Exception? PostOwnerSignal()
    {
        if (_window.IsNull)
        {
            var exception = new InvalidOperationException(
                "The overlay owner window is not available for an owner action.");
            ReportDiagnostic(exception);
            CancelStartup();
            return exception;
        }

        if (PInvoke.PostMessage(_window, HostCommandMessage, 0, 0))
        {
            return null;
        }

        var failure = LastWin32Error("PostMessage");
        ReportDiagnostic(failure);
        CancelStartup();
        return failure;
    }

    private void ReportOwnerPostFailure(Exception exception) => ReportDiagnostic(exception);

    private void HandleMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (_ownerMessageRouter.Dispatch(message))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            ReportDiagnostic(exception);
            return;
        }

        try
        {
            _systemMessageHandler?.Invoke(message, (nint)wParam.Value, (nint)lParam.Value);
        }
        catch (Exception exception)
        {
            ReportDiagnostic(exception);
        }

        try
        {
            switch (message)
            {
            case WmLButtonDown:
                if (TryArmActionSurfacePointer(lParam)) break;
                _petBodyPointerArmed = BeginDrag(lParam);
                break;
            case WmMouseMove:
                ContinueDrag(lParam);
                break;
            case WmLButtonUp:
                var releasedPoint = GetClientPoint(lParam);
                var toggleActionSurface = _petBodyPointerArmed
                    && Math.Abs(releasedPoint.X - _dragOriginX) <= 4
                    && Math.Abs(releasedPoint.Y - _dragOriginY) <= 4;
                _petBodyPointerArmed = false;
                CommitPlacementIfDirty();
                ReleasePointerCapture();
                if (_actionSurfacePointerArmed)
                {
                    _actionSurfacePointerArmed = false;
                    _ = TryHandleActionSurfacePointer(lParam);
                }
                else if (toggleActionSurface && _actionSurface is not null)
                {
                    _actionSurface.ToggleFromPetBody(
                        new PixelRect(0, 0, _windowBounds.Width, _windowBounds.Height),
                        new PixelPoint(releasedPoint.X, releasedPoint.Y));
                }
                break;
            case WmLButtonDoubleClick:
                ReleasePointerCapture();
                _petBodyPointerArmed = false;
                _ = OverlayNativeCallbackObserver.ObserveAsync(
                    _openHome(CancellationToken.None),
                    _diagnostic);
                break;
            case WmRButtonUp:
                _showContextMenu();
                break;
            case WmMouseWheel:
                ChangeScale((short)((long)wParam.Value >> 16));
                break;
            case WmDpiChanged:
                ApplyDpiSuggestedRect(lParam, wParam);
                break;
            case WmDisplayChange:
                ResolveAndMove();
                break;
            case WmCancelMode:
            case WmCaptureChanged:
                CommitPlacementIfDirty();
                ReleasePointerCapture();
                _actionSurfacePointerArmed = false;
                _armedOverlayAction = null;
                _petBodyPointerArmed = false;
                break;
            case WmDestroy:
                CommitPlacementIfDirty();
                ReleasePointerCapture();
                _actionDispatchQueue.Dispose();
                Hosts.TryRemove((nint)_window.Value, out _);
                if (!_shutdownIssued)
                {
                    _shutdownIssued = true;
                    PInvoke.PostQuitMessage(0);
                }
                break;
            }
        }
        catch (Exception exception)
        {
            // Per-message containment: report and continue the message loop.
            ReportDiagnostic(exception);
        }
    }

    private bool BeginDrag(LPARAM lParam)
    {
        var point = GetClientPoint(lParam);
        if (_presenter is not LayeredFramePresenter layered
            || !layered.IsInteractiveAt(point.X, point.Y, CurrentBubbleHitRegions()))
        {
            return false;
        }

        if (!TryConfirmPointerCapture(
                (nint)_window.Value,
                hwnd => (nint)PInvoke.SetCapture(new HWND((void*)hwnd)).Value,
                () => (nint)PInvoke.GetCapture().Value,
                () => _ = PInvoke.ReleaseCapture()))
        {
            ReportDiagnostic(new InvalidOperationException("SetCapture did not establish capture."));
            return false;
        }

        _dragging = true;
        _dragOriginX = point.X;
        _dragOriginY = point.Y;
        if (PInvoke.GetCursorPos(out var cursor))
        {
            _dragOriginScreenX = cursor.X;
            _dragOriginScreenY = cursor.Y;
        }
        else
        {
            _dragOriginScreenX = _windowBounds.X + point.X;
            _dragOriginScreenY = _windowBounds.Y + point.Y;
        }
        _dragStartBounds = _windowBounds;
        _placementDirty = false;
        return true;
    }

    private void ContinueDrag(LPARAM lParam)
    {
        if (!_dragging)
        {
            return;
        }

        var point = GetClientPoint(lParam);
        if (Math.Abs(point.X - _dragOriginX) > 4 || Math.Abs(point.Y - _dragOriginY) > 4)
        {
            _petBodyPointerArmed = false;
        }
        var cursor = PInvoke.GetCursorPos(out var screenPoint)
            ? new PixelPoint(screenPoint.X, screenPoint.Y)
            : new PixelPoint(_windowBounds.X + point.X, _windowBounds.Y + point.Y);
        var bounds = CalculateDraggedBounds(
            _dragStartBounds,
            new PixelPoint(_dragOriginScreenX, _dragOriginScreenY),
            cursor);
        ApplyWindowState(bounds, _placement.Scale);
        _placement = MonitorPlacementService.Capture(
            bounds,
            _placement.Scale,
            _nominalSize,
            EnumerateMonitors());
        _placementDirty = true;
    }

    internal static PixelRect CalculateDraggedBounds(
        PixelRect startBounds,
        PixelPoint startCursor,
        PixelPoint currentCursor) =>
        new(
            startBounds.X + currentCursor.X - startCursor.X,
            startBounds.Y + currentCursor.Y - startCursor.Y,
            startBounds.Width,
            startBounds.Height);

    private void PersistPlacementAfterDrag()
    {
        if (_persistPlacementAsync is null)
        {
            return;
        }

        try
        {
            _ = OverlayNativeCallbackObserver.ObserveAsync(
                _persistPlacementAsync(_placement, CancellationToken.None),
                exception => ReportFailure("placement-save", exception));
        }
        catch (Exception exception)
        {
            ReportFailure("placement-save", exception);
        }
    }

    private void CommitPlacementIfDirty()
    {
        if (!_placementDirty)
        {
            return;
        }

        _placementDirty = false;
        PersistPlacementAfterDrag();
    }

    private void ChangeScale(short delta)
    {
        if (delta == 0)
        {
            return;
        }

        var factor = delta > 0 ? 1.1 : 1 / 1.1;
        _placement = _placement with
        {
            Scale = MonitorPlacementService.ClampScale(_placement.Scale * factor),
        };
        ResolveAndMove();
        _placementDirty = true;
    }

    private void ReleasePointerCapture()
    {
        if (_dragging)
        {
            _dragging = false;
            _ = PInvoke.ReleaseCapture();
        }
    }

    private void ShutdownOnOwnerThread()
    {
        if (_shutdownIssued)
        {
            return;
        }

        _shutdownIssued = true;
        _actionDispatchQueue.Dispose();
        _ownerActions.Close(new ObjectDisposedException(nameof(OverlayWindowHost)));
        CommitPlacementIfDirty();
        ReleasePointerCapture();
        if (!_window.IsNull)
        {
            if (!PInvoke.DestroyWindow(_window))
            {
                ReportFailure("overlay-dispose", LastWin32Error("DestroyWindow"));
            }
        }
        PInvoke.PostQuitMessage(0);
    }

    private static (int X, int Y) GetClientPoint(LPARAM lParam)
    {
        var value = unchecked((long)lParam.Value);
        return ((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
    }

    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (Hosts.TryGetValue((nint)hwnd.Value, out var host))
        {
            try
            {
                if (message == WmMouseActivate)
                {
                    return new LRESULT(MA_NOACTIVATE);
                }

                if (message == WmNcHitTest)
                {
                    try
                    {
                        var point = GetClientPoint(lParam);
                        return new LRESULT(host.IsInteractive(
                            point.X - host._windowBounds.X,
                            point.Y - host._windowBounds.Y)
                            ? HTCLIENT
                            : HTTRANSPARENT);
                    }
                    catch (Exception exception)
                    {
                        host.ReportDiagnostic(exception);
                        return new LRESULT(HTTRANSPARENT);
                    }
                }

                host.HandleMessage(message, wParam, lParam);
            }
            catch (Exception exception)
            {
                // Per-message containment: report and continue to DefWindowProc.
                host.ReportDiagnostic(exception);
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool IsInteractive(int x, int y) =>
        _presenter is LayeredFramePresenter layered
        && layered.IsInteractiveAt(x, y, CurrentBubbleHitRegions());

    private IReadOnlyList<PixelRect>? CurrentBubbleHitRegions() =>
        _actionSurface is null ? _bubbleHitRegions : null;

    private bool TryArmActionSurfacePointer(LPARAM lParam)
    {
        _armedOverlayAction = null;
        _actionSurfacePointerArmed = false;
        if (_actionSurface is null || _presenter is not LayeredFramePresenter layered) return false;
        var point = GetClientPoint(lParam);
        var action = layered.FindPresentedOverlayActionAt(point.X, point.Y);
        if (action is null) return false;
        _armedOverlayAction = action;
        _actionSurfacePointerArmed = true;
        return true;
    }

    private bool TryHandleActionSurfacePointer(LPARAM lParam)
    {
        var armed = _armedOverlayAction;
        _armedOverlayAction = null;
        if (_actionSurface is null
            || _presenter is not LayeredFramePresenter layered
            || armed is null) return false;
        var point = GetClientPoint(lParam);
        var released = layered.FindPresentedOverlayActionAt(point.X, point.Y);
        if (released is null
            || !string.Equals(released.AutomationId, armed.AutomationId, StringComparison.Ordinal)) return false;
        _actionDispatchQueue.Enqueue(_actionSurface, released);
        return true;
    }

    private void WaitForActionDispatchCompletion()
    {
        try
        {
            if (!_actionDispatchQueue.Completion.Wait(ShutdownTimeout))
            {
                ReportFailure("overlay-dispose", new TimeoutException(
                    "Overlay pointer dispatch did not stop before disposal timed out."));
            }
        }
        catch (AggregateException exception) { ReportFailure("overlay-dispose", exception.Flatten()); }
    }

    public static bool TryConfirmPointerCapture(
        nint hwnd,
        Func<nint, nint> setCapture,
        Func<nint> getCapture,
        Action releaseCapture)
    {
        ArgumentNullException.ThrowIfNull(setCapture);
        ArgumentNullException.ThrowIfNull(getCapture);
        ArgumentNullException.ThrowIfNull(releaseCapture);
        if (hwnd == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hwnd));
        }

        _ = setCapture(hwnd);
        if (getCapture() == hwnd)
        {
            return true;
        }

        releaseCapture();
        return false;
    }

    private static unsafe BOOL MonitorCallback(
        HMONITOR monitor,
        HDC _hdc,
        RECT* _clipRect,
        LPARAM data)
    {
        var handle = GCHandle.FromIntPtr(data.Value);
        var context = (MonitorEnumerationContext)handle.Target!;
        try
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
            if (!PInvoke.GetMonitorInfo(monitor, ref info.monitorInfo))
            {
                throw LastWin32Error("GetMonitorInfo");
            }

            var work = info.monitorInfo.rcWork;
            var dpiX = 96u;
            var dpiY = 96u;
            var dpiResult = PInvoke.GetDpiForMonitor(
                monitor,
                MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                out dpiX,
                out dpiY);
            if (dpiResult.Failed)
            {
                dpiX = 96;
                dpiY = 96;
            }
            var effectiveDpi = MonitorPlacementService.ResolveEffectiveDpi(
                (uint)dpiResult,
                dpiX,
                dpiY,
                context.Report);
            context.Monitors.Add(new MonitorInfo(
                GetDeviceName(info),
                new PixelRect(work.left, work.top, work.right - work.left, work.bottom - work.top),
                effectiveDpi,
                (info.monitorInfo.dwFlags & 1) != 0));
            return true;
        }
        catch (Exception exception)
        {
            context.Error = exception;
            return false;
        }
    }

    private static string GetDeviceName(MONITORINFOEXW info)
    {
        return info.szDevice.ToString();
    }

    private void ReportDiagnostic(Exception exception)
    {
        try
        {
            _diagnostic(exception);
        }
        catch
        {
            global::System.Diagnostics.Debug.WriteLine($"Dudu overlay diagnostic callback failed: {exception}");
        }
    }

    /// <summary>
    /// Reports an overlay failure with its operation name through the shared
    /// AppHost sink when one is composed, falling back to the legacy
    /// per-host diagnostic and finally to a Trace line carrying the
    /// operation name plus the exception type/HResult only. Behavior is
    /// unchanged: reporting never throws and never alters the caller's
    /// fail-closed or best-effort path.
    /// </summary>
    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
        }

        ReportDiagnostic(exception);

        if (_errorReporter is null)
        {
            Trace.TraceError(
                "Dudu overlay operation '{0}' failed: {1} (0x{2:X8})",
                operation,
                exception.GetType().FullName,
                exception.HResult);
        }
    }

    private static InvalidOperationException LastWin32Error(string operation) =>
        new($"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}.");

    private static void DefaultDiagnostic(Exception exception) =>
        Trace.TraceError(
            "Dudu overlay diagnostic: {0} (0x{1:X8})",
            exception.GetType().FullName,
            exception.HResult);

    private sealed class MonitorEnumerationContext
    {
        private readonly Action<Exception> _diagnostic;

        public MonitorEnumerationContext(Action<Exception> diagnostic) => _diagnostic = diagnostic;

        public List<MonitorInfo> Monitors { get; } = [];

        public Exception? Error { get; set; }

        public void Report(Exception exception) => _diagnostic(exception);
    }
}

internal sealed class OverlayOwnerMessageRouter
{
    private readonly uint _ownerCommandMessage;
    private readonly uint _shutdownMessage;
    private readonly Action _drainOwnerActions;
    private readonly Action _shutdown;

    public OverlayOwnerMessageRouter(
        uint ownerCommandMessage,
        uint shutdownMessage,
        Action drainOwnerActions,
        Action shutdown)
    {
        _ownerCommandMessage = ownerCommandMessage;
        _shutdownMessage = shutdownMessage;
        _drainOwnerActions = drainOwnerActions ?? throw new ArgumentNullException(nameof(drainOwnerActions));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    public bool Dispatch(uint message)
    {
        if (message == _shutdownMessage)
        {
            _shutdown();
            return true;
        }

        if (message == _ownerCommandMessage)
        {
            _drainOwnerActions();
            return true;
        }

        return false;
    }
}

file static class OverlayWindowHostLifecycle
{
    public static async ValueTask WaitForStopAndActionDispatchAsync(OverlayWindowHost host)
    {
        await WaitForStopAsync(host).ConfigureAwait(false);
        await WaitForActionDispatchAsync(host).ConfigureAwait(false);
    }

    public static async ValueTask WaitForActionDispatchAsync(OverlayWindowHost host)
    {
        try
        {
            await host.ActionDispatchCompletion.WaitAsync(host.ShutdownBudget).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            host.ReportDisposalTimeout(new TimeoutException(
                "Overlay pointer dispatch did not stop before disposal timed out.",
                exception));
        }
    }

    public static async ValueTask WaitForStopAsync(OverlayWindowHost host)
    {
        if (!host.OwnerThreadIsAlive)
        {
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await host.StoppedTask.WaitAsync(host.ShutdownBudget).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            host.ReportDisposalTimeout(new TimeoutException(
                "Overlay owner thread did not stop before disposal timed out.",
                exception));
            return;
        }

        while (host.OwnerThreadIsAlive)
        {
            var remaining = host.ShutdownBudget - Stopwatch.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                host.ReportDisposalTimeout(new TimeoutException(
                    "Overlay owner thread signaled teardown but remained alive before DisposeAsync timed out."));
                return;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(10)
                    ? remaining
                    : TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    public static async Task<OverlayWindowHost> WaitForCreationAsync(
        OverlayWindowHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            return await host.CreationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            host.CancelCreation();
            host.JoinAfterCreationFailure();
            throw;
        }
    }
}
