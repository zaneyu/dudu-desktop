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
    private readonly object _activationGate = new();
    private readonly Queue<AppActivation> _pendingActivations = new();
    private IPrimaryAppRuntime? _runtime;
    private bool _started;
    private bool _runtimeReady;
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
            var runtime = await _runtimeFactory(cancellationToken);
            lock (_activationGate)
            {
                _runtime = runtime ?? throw new InvalidOperationException(
                    "The primary runtime factory returned null.");
            }

            await runtime.StartAsync(cancellationToken);
            AppActivation[] pending;
            lock (_activationGate)
            {
                _runtimeReady = true;
                pending = _pendingActivations.ToArray();
                _pendingActivations.Clear();
                _started = true;
            }

            foreach (var activation in pending)
            {
                try
                {
                    await runtime.ActivateAsync(activation, cancellationToken);
                }
                catch (Exception exception)
                {
                    global::System.Diagnostics.Trace.TraceError(
                        "Queued Dudu activation failed: {0}",
                        exception);
                }
            }

            return true;
        }
        catch
        {
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync();
                _runtime = null;
            }

            lock (_activationGate)
            {
                _runtime = null;
                _runtimeReady = false;
                _pendingActivations.Clear();
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

        lock (_activationGate)
        {
            _runtimeReady = false;
            _pendingActivations.Clear();
        }

        await _singleInstance.DisposeAsync();
    }

    private Task RouteActivationAsync(AppActivation activation)
    {
        IPrimaryAppRuntime? runtime;
        lock (_activationGate)
        {
            if (!_runtimeReady || _runtime is null)
            {
                _pendingActivations.Enqueue(activation);
                return Task.CompletedTask;
            }

            runtime = _runtime;
        }

        return runtime.ActivateAsync(activation);
    }
}

/// <summary>
/// Observes application bootstrap from the UI entry point. It owns partial
/// bootstrap disposal and turns factory/start failures into a diagnostic plus
/// one controlled process exit instead of an unobserved task exception.
/// </summary>
public sealed class CompanionStartupRunner
{
    private readonly Func<string, CancellationToken, Task<WindowsCompanionBootstrap>> _factory;
    private readonly Action<Exception> _report;
    private readonly Action _exit;
    private int _exitRequested;

    public CompanionStartupRunner(
        Func<string, CancellationToken, Task<WindowsCompanionBootstrap>> factory,
        Action<Exception> report,
        Action exit)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public WindowsCompanionBootstrap? Bootstrap { get; private set; }

    public async Task RunAsync(
        string arguments,
        CancellationToken cancellationToken = default)
    {
        WindowsCompanionBootstrap? bootstrap = null;
        try
        {
            bootstrap = await _factory(arguments, cancellationToken);
            if (bootstrap is null)
            {
                throw new InvalidOperationException("The bootstrap factory returned null.");
            }

            if (!await bootstrap.StartAsync(cancellationToken))
            {
                await bootstrap.DisposeAsync();
                RequestExit();
                return;
            }

            Bootstrap = bootstrap;
        }
        catch (Exception exception)
        {
            Report(exception);
            if (bootstrap is not null)
            {
                try { await bootstrap.DisposeAsync(); }
                catch (Exception disposeException) { Report(disposeException); }
            }

            RequestExit();
        }
    }

    private void Report(Exception exception)
    {
        try { _report(exception); }
        catch { }
    }

    private void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return;
        try { _exit(); }
        catch (Exception exception) { Report(exception); }
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
    private readonly bool _initialUserVisible;
    private readonly Func<Preferences, CancellationToken, Task>? _onPreferencesChanged;
    private bool _started;

    public WindowsCompanionRuntime(
        AppHost host,
        OverlayWindowHost overlay,
        AppLifecycleCoordinator lifecycle,
        TrayIconService tray,
        GlobalHotkeyService hotkey,
        ICompanionEventSource events,
        bool initialUserVisible,
        Func<Preferences, CancellationToken, Task>? onPreferencesChanged)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _initialUserVisible = initialUserVisible;
        _onPreferencesChanged = onPreferencesChanged;
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
        Func<AppLifecycleCoordinator, Action<TrayCommand>>? trayCommandHandlerFactory = null,
        Func<OverlayWindowHost, Task>? initializeOverlay = null,
        bool initialUserVisible = true,
        Func<Preferences, CancellationToken, Task>? onPreferencesChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openHome);
        if (trayCommandHandler is null && trayCommandHandlerFactory is null)
        {
            throw new ArgumentException(
                "A tray command handler or handler factory is required.",
                nameof(trayCommandHandler));
        }
        var fullscreen = new FullscreenDetector();
        isFullscreen ??= fullscreen.IsForegroundFullscreen;
        var events = new WindowsCompanionEventSource(fullscreen);
        OverlayWindowHost? overlay = null;
        AppLifecycleCoordinator? lifecycle = null;
        try
        {
            overlay = await OverlayWindowHost.CreateAsync(
                presenter,
                placement,
                nominalSize,
                openHome,
                systemMessageHandler: (message, wParam, lParam) =>
                {
                    _ = events.HandleWindowMessage(message, wParam, lParam);
                },
                cancellationToken: cancellationToken);
            var handler = trayCommandHandler
                ?? (command => trayCommandHandlerFactory!(lifecycle
                    ?? throw new InvalidOperationException("Lifecycle is not composed."))(command));
            var tray = new TrayIconService(commandHandler: handler);
            var hotkey = new GlobalHotkeyService();
            lifecycle = new AppLifecycleCoordinator(
                host,
                new OverlayLifecycleAdapter(overlay),
                pet,
                preferences,
                pauseState,
                isQuietHours,
                isFullscreen,
                clock,
                tray,
                openHome,
                initialUserVisible: initialUserVisible);
            if (initializeOverlay is not null)
            {
                await initializeOverlay(overlay);
            }
            return new WindowsCompanionRuntime(
                host,
                overlay,
                lifecycle!,
                tray,
                hotkey,
                events,
                initialUserVisible,
                onPreferencesChanged);
        }
        catch
        {
            if (lifecycle is not null)
            {
                await lifecycle.DisposeAsync();
            }
            else if (overlay is not null)
            {
                await overlay.DisposeAsync();
            }

            await events.DisposeAsync();
            throw;
        }
    }

    public async Task ApplySettingsAsync(
        Preferences preferences,
        PetPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(placement);
        await _lifecycle.UpdatePreferencesAsync(preferences, cancellationToken);
        await _overlay.SetAlwaysOnTopAsync(preferences.AlwaysOnTop, cancellationToken);
        if (_onPreferencesChanged is not null)
        {
            await _onPreferencesChanged(preferences, cancellationToken);
        }
        await _overlay.SetPlacementAsync(placement, cancellationToken);
    }

    public Task ApplyPlacementAsync(
        PetPlacement placement,
        CancellationToken cancellationToken = default) =>
        _overlay.SetPlacementAsync(placement, cancellationToken);

    public Task<MonitorPlacementSnapshot> CapturePlacementSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        _overlay.CapturePlacementSnapshotAsync(cancellationToken);

    public Task SetGlobalShortcutAsync(string shortcut, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gesture = HotkeyGesture.Parse(shortcut);
        return _overlay.InvokeOnOwnerAsync(() => _hotkey.SetGesture(gesture), cancellationToken);
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
                _tray.Attach(
                    _overlay.Handle,
                    action => _overlay.InvokeOnOwnerAsync(action));
            });
            await StartupVisibilityGate.ApplyAsync(
                token => _events.StartAsync(_overlay.Handle, this, token),
                _lifecycle.SetUserVisibleAsync,
                _initialUserVisible,
                cancellationToken);
            _started = true;
        }
        catch
        {
            _hotkey.Triggered -= OnHotkeyTriggered;
            await _events.DisposeAsync();
            await _lifecycle.DisposeAsync();
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

    public Task SetUserVisibleAsync(
        bool visible,
        CancellationToken cancellationToken = default) =>
        _lifecycle.SetUserVisibleAsync(visible, cancellationToken);

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

internal static class StartupVisibilityGate
{
    public static async Task ApplyAsync(
        Func<CancellationToken, Task> sampleFullscreenAsync,
        Func<bool, CancellationToken, Task> setUserVisibleAsync,
        bool desiredVisible,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleFullscreenAsync);
        ArgumentNullException.ThrowIfNull(setUserVisibleAsync);

        await sampleFullscreenAsync(cancellationToken);
        await setUserVisibleAsync(desiredVisible, cancellationToken);
    }
}
