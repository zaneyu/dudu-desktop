using Dudu.App.Overlay;
using Dudu.App.Animation;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Assets;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Dudu.App.Hosting;

public interface IPrimaryAppRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task ActivateAsync(AppActivation activation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the process gate. No primary-only object is created until the mutex
/// decision and secondary activation have completed.
/// </summary>
public sealed class WindowsCompanionBootstrap : IAsyncDisposable
{
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly Func<CancellationToken, Task<IPrimaryAppRuntime>> _runtimeFactory;
    private IPrimaryAppRuntime? _runtime;
    private bool _started;
    private bool _disposed;

    public WindowsCompanionBootstrap(
        Func<CancellationToken, Task<IPrimaryAppRuntime>> runtimeFactory,
        IActivationTransport? transport = null)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _singleInstance = new SingleInstanceCoordinator(transport, RouteActivationAsync);
    }

    public bool IsPrimary => _singleInstance.IsPrimary;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsCompanionBootstrap));
        if (_started) return true;

        if (!await _singleInstance.TryAcquireAsync(cancellationToken))
        {
            // The secondary has already sent its one-byte activation. It must
            // not construct AppHost, the overlay, tray, or hotkey services.
            await _singleInstance.DisposeAsync();
            return false;
        }

        try
        {
            _runtime = await _runtimeFactory(cancellationToken);
            await _runtime.StartAsync(cancellationToken);
            _started = true;
            return true;
        }
        catch
        {
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync();
                _runtime = null;
            }

            await _singleInstance.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        await _singleInstance.DisposeAsync();
    }

    private Task RouteActivationAsync(AppActivation activation)
    {
        var runtime = _runtime;
        return runtime is null
            ? Task.CompletedTask
            : runtime.ActivateAsync(activation);
    }
}

public interface ICompanionEventSink
{
    Task OnSessionLockedAsync(CancellationToken cancellationToken = default);
    Task OnSessionUnlockedAsync(CancellationToken cancellationToken = default);
    Task OnSuspendAsync(CancellationToken cancellationToken = default);
    Task OnResumeAsync(CancellationToken cancellationToken = default);
    Task OnDisplayChangedAsync(CancellationToken cancellationToken = default);
    Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default);
    Task OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken = default);
    bool HandleWindowMessage(uint message, nint wParam, nint lParam);
}

public interface ICompanionEventSource : IAsyncDisposable
{
    Task StartAsync(
        nint ownerWindow,
        ICompanionEventSink sink,
        CancellationToken cancellationToken = default);

    bool HandleWindowMessage(uint message, nint wParam, nint lParam);
}

/// <summary>
/// Bridges the overlay's real message queue and a bounded fullscreen poll to
/// the lifecycle coordinator. The interface is intentionally replaceable in
/// tests; this implementation is the Windows production seam.
/// </summary>
public sealed class WindowsCompanionEventSource : ICompanionEventSource
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint PbtApmsuspend = 0x0004;
    private const uint PbtResumeSuspend = 0x0007;
    private const uint PbtResumeAutomatic = 0x0012;
    private const uint WtsSessionLock = 0x0007;
    private const uint WtsSessionUnlock = 0x0008;
    private const int NotifyForThisSession = 0;

    private readonly FullscreenDetector _fullscreen;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _pollTask;
    private ICompanionEventSink? _sink;
    private nint _ownerWindow;
    private bool _lastFullscreen;
    private bool _registered;

    public WindowsCompanionEventSource(
        FullscreenDetector? fullscreen = null,
        TimeSpan? pollInterval = null)
    {
        _fullscreen = fullscreen ?? new FullscreenDetector();
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task StartAsync(
        nint ownerWindow,
        ICompanionEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (ownerWindow == 0) throw new ArgumentOutOfRangeException(nameof(ownerWindow));
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows companion events require Windows.");
        }

        lock (_gate)
        {
            if (_stop is not null) return;
            _ownerWindow = ownerWindow;
            _sink = sink;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _registered = PInvoke.WTSRegisterSessionNotification(
                ToHwnd(ownerWindow),
                NotifyForThisSession);
            _lastFullscreen = _fullscreen.IsForegroundFullscreen();
            _pollTask = PollFullscreenAsync(_stop.Token);
        }

        await sink.OnFullscreenChangedAsync(_lastFullscreen, cancellationToken);
    }

    public bool HandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        ICompanionEventSink? sink;
        lock (_gate) sink = _sink;
        if (sink is null) return false;

        if (sink.HandleWindowMessage(message, wParam, lParam)) return true;

        var handled = false;
        switch (message)
        {
            case WmWtsSessionChange:
                switch (unchecked((uint)wParam))
                {
                    case WtsSessionLock:
                        _ = Observe(sink.OnSessionLockedAsync());
                        handled = true;
                        break;
                    case WtsSessionUnlock:
                        _ = Observe(sink.OnSessionUnlockedAsync());
                        handled = true;
                        break;
                }
                break;
            case WmDisplayChange:
                _ = Observe(sink.OnDisplayChangedAsync());
                handled = true;
                break;
            case WmPowerBroadcast:
                if (unchecked((uint)wParam) == PbtApmsuspend)
                {
                    _ = Observe(sink.OnSuspendAsync());
                    handled = true;
                }
                else if (unchecked((uint)wParam) is PbtResumeSuspend or PbtResumeAutomatic)
                {
                    _ = Observe(sink.OnResumeAsync());
                    handled = true;
                }
                break;
            default:
                if (message == TrayIconService.TaskbarCreatedMessage)
                {
                    _ = Observe(sink.OnTaskbarCreatedAsync());
                    handled = true;
                }
                break;
        }

        return handled;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stop;
        Task? poll;
        lock (_gate)
        {
            stop = _stop;
            poll = _pollTask;
            _stop = null;
            _pollTask = null;
            if (_registered && _ownerWindow != 0 && OperatingSystem.IsWindows())
            {
                _ = PInvoke.WTSUnRegisterSessionNotification(ToHwnd(_ownerWindow));
            }

            _registered = false;
            _sink = null;
        }

        if (stop is not null)
        {
            stop.Cancel();
            if (poll is not null)
            {
                try { await poll; }
                catch (OperationCanceledException) { }
            }

            stop.Dispose();
        }
    }

    private async Task PollFullscreenAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var fullscreen = _fullscreen.IsForegroundFullscreen();
            ICompanionEventSink? sink;
            lock (_gate) sink = _sink;
            if (sink is not null && fullscreen != _lastFullscreen)
            {
                _lastFullscreen = fullscreen;
                await sink.OnFullscreenChangedAsync(fullscreen, cancellationToken);
            }
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch { }
    }

    private static unsafe HWND ToHwnd(nint hwnd) => new((void*)hwnd);
}

public sealed class WindowsCompanionRuntime : IPrimaryAppRuntime, ICompanionEventSink
{
    private readonly AppHost _host;
    private readonly OverlayWindowHost _overlay;
    private readonly AppLifecycleCoordinator _lifecycle;
    private readonly TrayIconService _tray;
    private readonly GlobalHotkeyService _hotkey;
    private readonly ICompanionEventSource _events;
    private bool _started;

    public WindowsCompanionRuntime(
        AppHost host,
        OverlayWindowHost overlay,
        AppLifecycleCoordinator lifecycle,
        TrayIconService tray,
        GlobalHotkeyService hotkey,
        ICompanionEventSource events)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public static async Task<WindowsCompanionRuntime> CreateAsync(
        AppHost host,
        IFramePresenter presenter,
        PetPlacement placement,
        PixelSize nominalSize,
        PetStateMachine pet,
        Preferences preferences,
        Func<PauseState>? pauseState = null,
        Func<bool>? isQuietHours = null,
        Func<bool>? isFullscreen = null,
        Func<DateTimeOffset>? clock = null,
        Action? openHome = null,
        Action<TrayCommand>? trayCommandHandler = null,
        CancellationToken cancellationToken = default)
    {
        var events = new WindowsCompanionEventSource();
        try
        {
            var overlay = await OverlayWindowHost.CreateAsync(
                presenter,
                placement,
                nominalSize,
                openHome,
                systemMessageHandler: (message, wParam, lParam) =>
                {
                    _ = events.HandleWindowMessage(message, wParam, lParam);
                },
                cancellationToken: cancellationToken);
            var tray = new TrayIconService(commandHandler: trayCommandHandler);
            var hotkey = new GlobalHotkeyService();
            var lifecycle = new AppLifecycleCoordinator(
                host,
                new OverlayLifecycleAdapter(overlay),
                pet,
                preferences,
                pauseState,
                isQuietHours,
                isFullscreen,
                clock,
                tray,
                openHome);
            return new WindowsCompanionRuntime(host, overlay, lifecycle, tray, hotkey, events);
        }
        catch
        {
            await events.DisposeAsync();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        await _host.StartAsync(cancellationToken);
        _hotkey.Triggered += OnHotkeyTriggered;
        try
        {
            await _overlay.InvokeOnOwnerAsync(() =>
            {
                _hotkey.AttachOwnerWindow(_overlay.Handle);
                _hotkey.SetGesture(HotkeyGesture.Default);
                _tray.Attach(_overlay.Handle, _overlay.InvokeOnOwnerAsync);
            });
            await _events.StartAsync(_overlay.Handle, this, cancellationToken);
            _started = true;
        }
        catch
        {
            _hotkey.Triggered -= OnHotkeyTriggered;
            await _host.StopAsync();
            throw;
        }
    }

    public Task ActivateAsync(
        AppActivation activation,
        CancellationToken cancellationToken = default) => activation switch
        {
            AppActivation.OpenHome => _lifecycle.OnHotkeyAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(activation)),
        };

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            await _events.DisposeAsync();
            await _lifecycle.DisposeAsync();
            return;
        }

        await _events.DisposeAsync();
        _hotkey.Triggered -= OnHotkeyTriggered;
        await _overlay.InvokeOnOwnerAsync(_hotkey.Dispose);
        await _lifecycle.DisposeAsync();
        _started = false;
    }

    public Task OnSessionLockedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnSessionLockedAsync(cancellationToken);

    public Task OnSessionUnlockedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnSessionUnlockedAsync(cancellationToken);

    public Task OnSuspendAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnSuspendAsync(cancellationToken);

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnResumeAsync(cancellationToken);

    public Task OnDisplayChangedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnDisplayChangedAsync(cancellationToken);

    public Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnTaskbarCreatedAsync(cancellationToken);

    public Task OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken = default) =>
        _lifecycle.OnFullscreenChangedAsync(fullscreen, cancellationToken);

    public bool HandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        return _hotkey.HandleMessage(message, wParam)
            || _tray.HandleWindowMessage(message, wParam, lParam);
    }

    private void OnHotkeyTriggered(object? sender, EventArgs args) =>
        _ = Observe(_lifecycle.OnHotkeyAsync());

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch { }
    }
}
