using System.Diagnostics;
using Dudu.App.Overlay;
using Dudu.App.Animation;
using Dudu.App.Presentation;
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
    private readonly object _lifecycleGate = new();
    private readonly Queue<AppActivation> _pendingActivations = new();
    private IPrimaryAppRuntime? _runtime;
    private bool _runtimeReady;
    private bool _disposed;
    private Task<bool>? _startTask;
    private Task? _disposeTask;

    public WindowsCompanionBootstrap(
        Func<CancellationToken, Task<IPrimaryAppRuntime>> runtimeFactory,
        IActivationTransport? transport = null)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _singleInstance = new SingleInstanceCoordinator(transport, RouteActivationAsync);
    }

    public bool IsPrimary => _singleInstance.IsPrimary;

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return Task.FromException<bool>(
                    new ObjectDisposedException(nameof(WindowsCompanionBootstrap)));
            }

            if (_startTask is null)
            {
                _startTask = StartCoreAsync(cancellationToken);
            }

            return WaitForCallerAsync(_startTask, cancellationToken);
        }
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _singleInstance.TryAcquireAsync(cancellationToken))
        {
            // The secondary has already sent its one-byte activation. It must
            // not construct AppHost, the overlay, tray, or hotkey services.
            ReportSecondaryExit();
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
            }

            foreach (var activation in pending)
            {
                try
                {
                    await runtime.ActivateAsync(activation, cancellationToken);
                }
                catch (Exception exception)
                {
                    Trace.TraceError(
                        "Queued Dudu activation failed: {0} (0x{1:X8})",
                        exception.GetType().FullName,
                        exception.HResult);
                }
            }

            return true;
        }
        catch
        {
            try
            {
                if (_runtime is not null)
                {
                    await _runtime.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Dudu partial bootstrap runtime cleanup failed: {0} (0x{1:X8})",
                    exception.GetType().FullName,
                    exception.HResult);
            }

            lock (_activationGate)
            {
                _runtime = null;
                _runtimeReady = false;
                _pendingActivations.Clear();
            }

            try { await _singleInstance.DisposeAsync(); }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Dudu partial bootstrap instance cleanup failed: {0} (0x{1:X8})",
                    exception.GetType().FullName,
                    exception.HResult);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync(_startTask);
            }

            disposeTask = _disposeTask;
        }

        await disposeTask;
    }

    private async Task DisposeCoreAsync(Task<bool>? startTask)
    {
        if (startTask is not null)
        {
            try { await startTask; }
            catch { }
        }

        IPrimaryAppRuntime? runtime;
        lock (_activationGate)
        {
            runtime = _runtime;
            _runtime = null;
            _runtimeReady = false;
            _pendingActivations.Clear();
        }

        try
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }
        finally
        {
            await _singleInstance.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForCallerAsync(
        Task<bool> operation,
        CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? await operation.WaitAsync(cancellationToken)
            : await operation;

    /// <summary>
    /// Records the secondary-instance exit to Trace and the
    /// <c>diagnostics.log</c> file sink. A secondary never touches the crash
    /// counter (only primary runs count as failed runs), so without this line
    /// its silent exit was indistinguishable from a crash that left no trace.
    /// Best-effort and never throws.
    /// </summary>
    private static void ReportSecondaryExit()
    {
        const string message =
            "Dudu secondary instance forwarded activation to the primary and is exiting.";
        Trace.TraceInformation(message);
        try
        {
            FileDiagnosticLoggerProvider.AppendRedactedLine(
                AppPaths.ForCurrentUser().Logs,
                "Dudu.SingleInstance",
                Microsoft.Extensions.Logging.LogLevel.Information,
                message);
        }
        catch
        {
        }
    }

    private Task RouteActivationAsync(AppActivation activation)
    {
        IPrimaryAppRuntime? runtime;
        lock (_activationGate)
        {
            if (!_runtimeReady || _runtime is null)
            {
                // Coalesce pre-ready activations at 1: every activation is
                // currently OpenHome, so queuing more only replays identical
                // work after StartAsync.
                if (_pendingActivations.Count == 0)
                {
                    _pendingActivations.Enqueue(activation);
                }

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
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly object _gate = new();
    private readonly object _callbackSync = new();
    private readonly SemaphoreSlim _callbackGate = new(1, 1);
    private readonly HashSet<Task> _callbackTasks = new();
    private CancellationTokenSource? _stop;
    private Task? _pollTask;
    private ICompanionEventSink? _sink;
    private nint _ownerWindow;
    private bool _lastFullscreen;
    private bool _registered;
    private bool _callbacksClosed;

    public WindowsCompanionEventSource(
        FullscreenDetector? fullscreen = null,
        TimeSpan? pollInterval = null,
        IAppHostErrorReporter? errorReporter = null)
    {
        _fullscreen = fullscreen ?? new FullscreenDetector();
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _errorReporter = errorReporter;
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
            try
            {
                _lastFullscreen = _fullscreen.IsForegroundFullscreen();
            }
            catch
            {
                // Fail-closed: a broken initial sample hides rather than shows.
                _lastFullscreen = true;
            }
            _pollTask = PollFullscreenAsync(_stop.Token);
        }

        await RunCallbackAsync(
            () => sink.OnFullscreenChangedAsync(_lastFullscreen, cancellationToken),
            cancellationToken);
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
                        TrackCallback(() => sink.OnSessionLockedAsync(), "session-lock");
                        handled = true;
                        break;
                    case WtsSessionUnlock:
                        TrackCallback(() => sink.OnSessionUnlockedAsync(), "session-unlock");
                        handled = true;
                        break;
                }
                break;
            case WmDisplayChange:
                TrackCallback(() => sink.OnDisplayChangedAsync(), "display-change");
                handled = true;
                break;
            case WmPowerBroadcast:
                if (unchecked((uint)wParam) == PbtApmsuspend)
                {
                    TrackCallback(() => sink.OnSuspendAsync(), "suspend");
                    handled = true;
                }
                else if (unchecked((uint)wParam) is PbtResumeSuspend or PbtResumeAutomatic)
                {
                    TrackCallback(() => sink.OnResumeAsync(), "resume");
                    handled = true;
                }
                break;
            default:
                if (message == TrayIconService.TaskbarCreatedMessage)
                {
                    TrackCallback(() => sink.OnTaskbarCreatedAsync(), "taskbar-created");
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
            _callbacksClosed = true;
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

        await DrainCallbacksAsync();
    }

    private async Task PollFullscreenAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            bool fullscreen;
            try
            {
                // Live re-sample on every tick; fail-closed to hidden on error
                // so a broken query never leaves the pet over fullscreen.
                fullscreen = _fullscreen.IsForegroundFullscreen();
            }
            catch (Exception exception)
            {
                // Fail-closed to hidden is preserved; the failure now also
                // leaves an operation-named diagnostic instead of only Trace.
                ReportFailure("fullscreen-poll", exception);
                fullscreen = true;
            }

            ICompanionEventSink? sink;
            lock (_gate) sink = _sink;
            if (sink is not null && fullscreen != _lastFullscreen)
            {
                _lastFullscreen = fullscreen;
                // Do not serialize the poll behind _callbackGate: a slow
                // session/suspend callback must not delay fullscreen hiding.
                // Fire-and-forget with tracking so DisposeAsync still drains.
                TrackCallbackWithoutGate(
                    () => sink.OnFullscreenChangedAsync(fullscreen, cancellationToken),
                    "fullscreen-poll");
            }
        }
    }

    private void TrackCallback(Func<Task> callback, string operation)
    {
        Task task;
        lock (_gate)
        {
            if (_callbacksClosed || _sink is null)
            {
                return;
            }

            task = RunCallbackAsync(callback, CancellationToken.None);
            lock (_callbackSync)
            {
                _callbackTasks.Add(task);
            }
        }

        _ = ForgetCallbackAsync(task, operation);
    }

    private void TrackCallbackWithoutGate(Func<Task> callback, string operation)
    {
        Task task;
        lock (_gate)
        {
            if (_callbacksClosed || _sink is null)
            {
                return;
            }

            task = callback();
            lock (_callbackSync)
            {
                _callbackTasks.Add(task);
            }
        }

        _ = ForgetCallbackAsync(task, operation);
    }

    private async Task ForgetCallbackAsync(Task task, string operation)
    {
        try { await task; }
        catch (Exception exception)
        {
            ReportFailure(operation, exception);
        }
        finally
        {
            lock (_callbackSync)
            {
                _callbackTasks.Remove(task);
            }
        }
    }

    private void ReportFailure(string operation, Exception exception) =>
        WindowsCompanionRuntime.ReportStaticFailure(_errorReporter, operation, exception);

    private async Task RunCallbackAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        await _callbackGate.WaitAsync(cancellationToken);
        try
        {
            await callback();
        }
        finally
        {
            _callbackGate.Release();
        }
    }

    private async Task DrainCallbacksAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_callbackSync) pending = _callbackTasks.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); }
            catch { }
        }
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
    private readonly IPresentationEnvironmentSink? _presentationEnvironment;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly object _lifecycleGate = new();
    private readonly object _callbackSync = new();
    private readonly HashSet<Task> _callbackTasks = new();
    private Task<bool>? _startTask;
    private Task? _disposeTask;
    private bool _callbacksClosed;
    private bool _disposed;

    public WindowsCompanionRuntime(
        AppHost host,
        OverlayWindowHost overlay,
        AppLifecycleCoordinator lifecycle,
        TrayIconService tray,
        GlobalHotkeyService hotkey,
        ICompanionEventSource events,
        bool initialUserVisible,
        Func<Preferences, CancellationToken, Task>? onPreferencesChanged,
        IPresentationEnvironmentSink? presentationEnvironment = null,
        IAppHostErrorReporter? errorReporter = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _initialUserVisible = initialUserVisible;
        _onPreferencesChanged = onPreferencesChanged;
        _presentationEnvironment = presentationEnvironment;
        _errorReporter = errorReporter;
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
        Func<CancellationToken, Task>? openHome = null,
        Action<TrayCommand>? trayCommandHandler = null,
        Func<AppLifecycleCoordinator, Action<TrayCommand>>? trayCommandHandlerFactory = null,
        Func<OverlayWindowHost, Task>? initializeOverlay = null,
        bool initialUserVisible = true,
        Func<Preferences, CancellationToken, Task>? onPreferencesChanged = null,
        IPresentationEnvironmentSink? presentationEnvironment = null,
        CancellationToken cancellationToken = default,
        IAppHostErrorReporter? errorReporter = null)
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
        var events = new WindowsCompanionEventSource(fullscreen, errorReporter: errorReporter);
        OverlayWindowHost? overlay = null;
        TrayIconService? tray = null;
        GlobalHotkeyService? hotkey = null;
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
                cancellationToken: cancellationToken,
                errorReporter: errorReporter);
            var handler = trayCommandHandler
                ?? (command => trayCommandHandlerFactory!(lifecycle
                    ?? throw new InvalidOperationException("Lifecycle is not composed."))(command));
            tray = new TrayIconService(commandHandler: handler, errorReporter: errorReporter);
            hotkey = new GlobalHotkeyService(errorReporter: errorReporter);
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
                initialUserVisible: initialUserVisible,
                errorReporter: errorReporter);
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
                onPreferencesChanged,
                presentationEnvironment,
                errorReporter);
        }
        catch
        {
            var lifecycleCleanupFailed = false;
            if (lifecycle is not null)
            {
                try { await lifecycle.DisposeAsync(); }
                catch (Exception exception)
                {
                    lifecycleCleanupFailed = true;
                    ReportStaticFailure(errorReporter, "partial-startup-lifecycle-cleanup", exception);
                }
            }
            if (lifecycle is null || lifecycleCleanupFailed)
            {
                try
                {
                    if (overlay is not null) await overlay.DisposeAsync();
                }
                catch (Exception exception)
                {
                    ReportStaticFailure(errorReporter, "partial-startup-overlay-cleanup", exception);
                }
            }

            try { hotkey?.Dispose(); }
            catch (Exception exception)
            {
                ReportStaticFailure(errorReporter, "partial-startup-hotkey-cleanup", exception);
            }
            if (lifecycle is null || lifecycleCleanupFailed)
            {
                if (tray is not null)
                {
                    try { await tray.DisposeAsync(); }
                    catch (Exception exception)
                    {
                        ReportStaticFailure(errorReporter, "partial-startup-tray-cleanup", exception);
                    }
                }
            }

            try { await events.DisposeAsync(); }
            catch (Exception exception)
            {
                ReportStaticFailure(errorReporter, "partial-startup-event-cleanup", exception);
            }
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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return Task.FromException(
                    new ObjectDisposedException(nameof(WindowsCompanionRuntime)));
            }

            _startTask ??= StartCoreAsync(cancellationToken);
            return cancellationToken.CanBeCanceled
                ? _startTask.WaitAsync(cancellationToken)
                : _startTask;
        }
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _host.StartAsync(cancellationToken);
            _hotkey.Triggered += OnHotkeyTriggered;
            await _overlay.InvokeOnOwnerAsync(() =>
            {
                try
                {
                    _hotkey.AttachOwnerWindow(_overlay.Handle);
                    _hotkey.SetGesture(HotkeyGesture.Default);
                }
                catch (Exception exception)
                {
                    // The global shortcut is convenience-only: another app may
                    // already own Ctrl+Alt+D. Losing it must never fail startup.
                    ReportFailure("hotkey-attach", exception);
                    Trace.TraceWarning("Dudu global hotkey unavailable: {0}", exception.Message);
                }

                try
                {
                    _tray.Attach(
                        _overlay.Handle,
                        action => _overlay.InvokeOnOwnerAsync(action));
                }
                catch (Exception exception)
                {
                    // The tray is best-effort too: Explorer may be restarting
                    // or the notification area unavailable. The overlay and
                    // settings remain usable, and the icon is recreated on
                    // TaskbarCreated.
                    ReportFailure("tray-attach", exception);
                    Trace.TraceWarning("Dudu tray icon unavailable: {0}", exception.Message);
                }
            });
            await StartupVisibilityGate.ApplyAsync(
                token => _events.StartAsync(_overlay.Handle, this, token),
                _lifecycle.SetUserVisibleAsync,
                _initialUserVisible,
                cancellationToken);
            return true;
        }
        catch
        {
            _hotkey.Triggered -= OnHotkeyTriggered;
            try { await _events.DisposeAsync(); }
            catch (Exception exception)
            {
                ReportFailure("partial-runtime-event-cleanup", exception);
            }
            try { await _overlay.InvokeOnOwnerAsync(_hotkey.Dispose); }
            catch (Exception exception)
            {
                ReportFailure("partial-runtime-hotkey-cleanup", exception);
                try { _hotkey.Dispose(); }
                catch (Exception fallbackException)
                {
                    ReportFailure("partial-runtime-hotkey-fallback-dispose", fallbackException);
                }
            }
            try { await _lifecycle.DisposeAsync(); }
            catch (Exception exception)
            {
                ReportFailure("partial-runtime-lifecycle-cleanup", exception);
            }
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

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync(_startTask);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task<bool>? startTask)
    {
        if (startTask is not null)
        {
            try { await startTask; }
            catch { }
        }

        lock (_callbackSync) _callbacksClosed = true;
        try { await _events.DisposeAsync(); }
        catch (Exception exception)
        {
            ReportFailure("runtime-event-shutdown", exception);
        }
        _hotkey.Triggered -= OnHotkeyTriggered;
        try { await _overlay.InvokeOnOwnerAsync(_hotkey.Dispose); }
        catch (Exception exception)
        {
            ReportFailure("runtime-hotkey-shutdown", exception);
            try { _hotkey.Dispose(); }
            catch (Exception fallbackException)
            {
                ReportFailure("runtime-hotkey-fallback-dispose", fallbackException);
            }
        }
        await DrainCallbacksAsync();
        try { await _lifecycle.DisposeAsync(); }
        catch (Exception exception)
        {
            ReportFailure("runtime-lifecycle-shutdown", exception);
        }
    }

    private void TrackCallback(Func<Task> callback, string operation)
    {
        Task task;
        lock (_callbackSync)
        {
            if (_callbacksClosed)
            {
                return;
            }

            task = callback();
            _callbackTasks.Add(task);
        }
        _ = ObserveTrackedCallbackAsync(task, operation);
    }

    private async Task ObserveTrackedCallbackAsync(Task callback, string operation)
    {
        try
        {
            await callback;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(operation, exception);
        }
        lock (_callbackSync) _callbackTasks.Remove(callback);
    }

    private void ReportFailure(string operation, Exception exception) =>
        ReportStaticFailure(_errorReporter, operation, exception);

    private async Task DrainCallbacksAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_callbackSync) pending = _callbackTasks.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); }
            catch { }
        }
    }

    public Task OnSessionLockedAsync(CancellationToken cancellationToken = default)
    {
        _presentationEnvironment?.SetSessionLocked(true);
        return _lifecycle.OnSessionLockedAsync(cancellationToken);
    }

    public Task OnSessionUnlockedAsync(CancellationToken cancellationToken = default)
    {
        _presentationEnvironment?.SetSessionLocked(false);
        return _lifecycle.OnSessionUnlockedAsync(cancellationToken);
    }

    public Task OnSuspendAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnSuspendAsync(cancellationToken);

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnResumeAsync(cancellationToken);

    public Task OnDisplayChangedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnDisplayChangedAsync(cancellationToken);

    public Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.OnTaskbarCreatedAsync(cancellationToken);

    public Task OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken = default)
    {
        _presentationEnvironment?.SetFullscreen(fullscreen);
        return _lifecycle.OnFullscreenChangedAsync(fullscreen, cancellationToken);
    }

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
        TrackCallback(() => _lifecycle.OnHotkeyAsync(), "hotkey");

    /// <summary>
    /// Reports a partial-startup/shutdown cleanup failure from a static
    /// context through the shared AppHost sink when one was composed,
    /// falling back to a Trace line with the operation name plus the
    /// exception type/HResult only. Never throws and never changes the
    /// caller's cleanup behavior.
    /// </summary>
    internal static void ReportStaticFailure(
        IAppHostErrorReporter? errorReporter,
        string operation,
        Exception exception)
    {
        if (errorReporter is not null)
        {
            try { errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu runtime operation '{0}' failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
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
