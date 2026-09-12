using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.Overlay;

/// <summary>
/// Owns a single no-activate popup window and its message thread.
/// </summary>
public sealed unsafe class OverlayWindowHost : IFramePresenter, IDisposable
{
    private const uint HostCommandMessage = 0x8001;
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
    private const uint WmNcCreate = 0x0081;
    private const uint WmDestroy = 0x0002;
    private const uint WmCancelMode = 0x001F;
    private const uint WmCaptureChanged = 0x0215;
    private const nint MA_NOACTIVATE = 3;
    private const nint HTCLIENT = 1;
    private const nint HTTRANSPARENT = -1;
    private const int WheelDelta = 120;
    private static readonly object ClassGate = new();
    private static readonly string ClassName = "Dudu.DesktopCompanion.PetOverlay.v1";
    private static OverlayWindowHost? s_creatingHost;
    private static readonly ConcurrentDictionary<nint, OverlayWindowHost> Hosts = new();
    private static readonly HWND TopmostWindow = new((void*)(-1));
    private static readonly WNDPROC WindowProcedure = WindowProc;

    private readonly IFramePresenter _presenter;
    private readonly PixelSize _nominalSize;
    private readonly Action _openHome;
    private readonly Action _showContextMenu;
    private readonly IReadOnlyList<PixelRect> _bubbleHitRegions;
    private readonly ConcurrentQueue<Action> _ownerActions = new();
    private readonly TaskCompletionSource<OverlayWindowHost> _created =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private readonly object _stateGate = new();
    private PetPlacement _placement;
    private PixelRect _windowBounds;
    private HWND _window;
    private bool _dragging;
    private int _dragOriginX;
    private int _dragOriginY;
    private PixelRect _dragStartBounds;
    private bool _disposeRequested;
    private int _ownerThreadId;

    private OverlayWindowHost(
        IFramePresenter presenter,
        PetPlacement placement,
        PixelSize nominalSize,
        Action? openHome,
        Action? showContextMenu,
        IReadOnlyList<PixelRect>? bubbleHitRegions)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        if (nominalSize.Width <= 0 || nominalSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize));
        }

        _nominalSize = nominalSize;
        _openHome = openHome ?? (() => { });
        _showContextMenu = showContextMenu ?? (() => { });
        _bubbleHitRegions = bubbleHitRegions?.ToArray() ?? [];
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
        Action? openHome = null,
        Action? showContextMenu = null,
        IReadOnlyList<PixelRect>? bubbleHitRegions = null,
        CancellationToken cancellationToken = default)
    {
        var host = new OverlayWindowHost(
            presenter,
            placement,
            nominalSize,
            openHome,
            showContextMenu,
            bubbleHitRegions);
        host._ownerThread.Start();
        return cancellationToken.CanBeCanceled
            ? host._created.Task.WaitAsync(cancellationToken)
            : host._created.Task;
    }

    public nint Handle => (nint)_window.Value;

    public bool IsVisible { get; private set; }

    public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _presenter.PresentAsync(frame, cancellationToken);
    }

    public void Show() => PostToOwner(() =>
    {
        IsVisible = true;
        if (!PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE))
        {
            // ShowWindow returning false means the window was previously hidden; it is
            // not a Win32 failure and must not be treated as one.
        }
    });

    public void Hide() => PostToOwner(() =>
    {
        IsVisible = false;
        _ = PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_HIDE);
        ReleasePointerCapture();
    });

    public void SetPlacement(PetPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        PostToOwner(() =>
        {
            _placement = placement;
            ResolveAndMove();
        });
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
        }

        if (_window.IsNull)
        {
            _ownerThread.Join(TimeSpan.FromSeconds(2));
            return;
        }

        PostToOwner(() =>
        {
            ReleasePointerCapture();
            if (!_window.IsNull)
            {
                _ = PInvoke.DestroyWindow(_window);
            }
            PInvoke.PostQuitMessage(0);
        });
        if (_ownerThreadId != Environment.CurrentManagedThreadId)
        {
            _stopped.Task.GetAwaiter().GetResult();
        }
    }

    private void OwnerThreadMain()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The overlay requires Windows.");
            }

            _ = PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((void*)(-4)));
            RegisterWindowClass();

            var initial = MonitorPlacementService.Resolve(
                _placement,
                _nominalSize,
                EnumerateMonitors());
            _windowBounds = initial.WindowBounds;

            lock (ClassGate)
            {
                s_creatingHost = this;
                using var module = PInvoke.GetModuleHandle(null);
                fixed (char* className = ClassName)
                fixed (char* title = "Dudu")
                {
                    _window = PInvoke.CreateWindowEx(
                        WINDOW_EX_STYLE.WS_EX_LAYERED
                        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
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
                s_creatingHost = null;
            }

            if (_window.IsNull)
            {
                ThrowLastWin32Error("CreateWindowEx");
            }

            Hosts[(nint)_window.Value] = this;
            if (_presenter is LayeredFramePresenter layeredPresenter)
            {
                layeredPresenter.Attach((nint)_window.Value);
            }

            _created.TrySetResult(this);
            RunMessageLoop();
        }
        catch (Exception exception)
        {
            _created.TrySetException(exception);
        }
        finally
        {
            ReleasePointerCapture();
            if (!_window.IsNull)
            {
                Hosts.TryRemove((nint)_window.Value, out _);
                _window = HWND.Null;
            }

            _stopped.TrySetResult(true);
        }
    }

    private void RunMessageLoop()
    {
        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0) > 0)
        {
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
                lpfnWndProc = WindowProcedure,
                hInstance = new HINSTANCE(instance.DangerousGetHandle()),
            };

            using (instance)
            fixed (char* className = ClassName)
            {
                windowClass.lpszClassName = new PCWSTR(className);
                if (PInvoke.RegisterClassEx(windowClass) == 0
                    && Marshal.GetLastWin32Error() != 1410)
                {
                    ThrowLastWin32Error("RegisterClassEx");
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
        _windowBounds = result.WindowBounds;
        if (!PInvoke.SetWindowPos(
                _window,
                TopmostWindow,
                _windowBounds.X,
                _windowBounds.Y,
                _windowBounds.Width,
                _windowBounds.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
                | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW))
        {
            ThrowLastWin32Error("SetWindowPos");
        }
    }

    private IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitor = PInvoke.MonitorFromWindow(_window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.IsNull)
        {
            return [new MonitorInfo("CURRENT", new PixelRect(0, 0, 1920, 1040), 96, true)];
        }

        var nativeInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref nativeInfo))
        {
            ThrowLastWin32Error("GetMonitorInfo");
        }

        var work = nativeInfo.rcWork;
        return
        [
            new MonitorInfo(
                "CURRENT",
                new PixelRect(work.left, work.top, work.right - work.left, work.bottom - work.top),
                _window.IsNull ? 96 : (int)PInvoke.GetDpiForWindow(_window),
                IsPrimary: true),
        ];
    }

    private void PostToOwner(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_ownerThreadId == Environment.CurrentManagedThreadId)
        {
            action();
            return;
        }

        lock (_stateGate)
        {
            if (_disposeRequested && _window.IsNull)
            {
                return;
            }
        }

        _ownerActions.Enqueue(action);
        if (!_window.IsNull)
        {
            _ = PInvoke.PostMessage(_window, HostCommandMessage, 0, 0);
        }
    }

    private void DrainOwnerActions()
    {
        while (_ownerActions.TryDequeue(out var action))
        {
            action();
        }
    }

    private void HandleMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case HostCommandMessage:
                DrainOwnerActions();
                break;
            case WmMouseActivate:
                break;
            case WmNcHitTest:
                break;
            case WmLButtonDown:
                BeginDrag(lParam);
                break;
            case WmMouseMove:
                ContinueDrag(lParam);
                break;
            case WmLButtonUp:
                ReleasePointerCapture();
                break;
            case WmLButtonDoubleClick:
                _openHome();
                break;
            case WmRButtonUp:
                _showContextMenu();
                break;
            case WmMouseWheel:
                ChangeScale((short)((long)wParam.Value >> 16));
                break;
            case WmDpiChanged:
            case WmDisplayChange:
                ResolveAndMove();
                break;
            case WmCancelMode:
            case WmCaptureChanged:
                ReleasePointerCapture();
                break;
            case WmDestroy:
                ReleasePointerCapture();
                Hosts.TryRemove((nint)_window.Value, out _);
                break;
        }
    }

    private void BeginDrag(LPARAM lParam)
    {
        var point = GetClientPoint(lParam);
        if (_presenter is not LayeredFramePresenter layered
            || !layered.IsInteractiveAt(point.X, point.Y, _bubbleHitRegions))
        {
            return;
        }

        _dragging = true;
        _dragOriginX = point.X;
        _dragOriginY = point.Y;
        _dragStartBounds = _windowBounds;
        _ = PInvoke.SetCapture(_window);
    }

    private void ContinueDrag(LPARAM lParam)
    {
        if (!_dragging)
        {
            return;
        }

        var point = GetClientPoint(lParam);
        var x = _dragStartBounds.X + point.X - _dragOriginX;
        var y = _dragStartBounds.Y + point.Y - _dragOriginY;
        _windowBounds = new PixelRect(x, y, _windowBounds.Width, _windowBounds.Height);
        if (!PInvoke.SetWindowPos(
                _window,
                TopmostWindow,
                x,
                y,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER))
        {
            ThrowLastWin32Error("SetWindowPos");
        }
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
    }

    private void ReleasePointerCapture()
    {
        if (_dragging)
        {
            _dragging = false;
            _ = PInvoke.ReleaseCapture();
        }
    }

    private static (int X, int Y) GetClientPoint(LPARAM lParam)
    {
        var value = unchecked((long)lParam.Value);
        return ((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
    }

    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WmNcCreate && s_creatingHost is { } creating)
        {
            Hosts[(nint)hwnd.Value] = creating;
        }

        if (Hosts.TryGetValue((nint)hwnd.Value, out var host))
        {
            if (message == WmMouseActivate)
            {
                return new LRESULT(MA_NOACTIVATE);
            }

            if (message == WmNcHitTest)
            {
                var point = GetClientPoint(lParam);
                return new LRESULT(host.IsInteractive(
                    point.X - host._windowBounds.X,
                    point.Y - host._windowBounds.Y)
                    ? HTCLIENT
                    : HTTRANSPARENT);
            }

            host.HandleMessage(message, wParam, lParam);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool IsInteractive(int x, int y)
    {
        return _presenter is LayeredFramePresenter layered
            && layered.IsInteractiveAt(x, y, _bubbleHitRegions);
    }

    private static void ThrowLastWin32Error(string operation) =>
        throw new InvalidOperationException(
            $"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}.");
}
