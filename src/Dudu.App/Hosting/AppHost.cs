using System.Diagnostics;
using Dudu.Core.Reminders;
using Dudu.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dudu.App.Hosting;

public interface IAppHostDatabase : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IAppHostReminderService
{
    Task TickAsync(CancellationToken cancellationToken = default);
}

public interface IAppHostTimerFactory
{
    IAppHostTimer Create(TimeSpan interval);
}

public interface IAppHostTimer : IAsyncDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default);
}

public interface IAppHostErrorReporter
{
    void Report(string operation, Exception exception);
}

/// <summary>
/// The presentation gateway's lifecycle hooks as seen by <see cref="AppHost"/>.
/// Started once the database has initialized, ticked after every successful
/// reminder tick (reusing the existing 30-second scheduler instead of a new
/// timer), and disposed during shutdown cleanup. Attaching one is optional:
/// an <see cref="AppHost"/> with none attached behaves exactly as before.
/// </summary>
public interface IAppHostPresentationGateway : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task TickAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The relay sync background loop's lifecycle hooks as seen by <see cref="AppHost"/>.
/// Started once the reminder scheduler is running, and disposed during shutdown
/// cleanup. Attaching one is optional: production attaches it only when a relay
/// base URL is configured; an <see cref="AppHost"/> with none attached behaves
/// exactly as before.
/// </summary>
public interface IAppHostRemoteSync : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
}

public sealed class AppHost : IAsyncDisposable, IAppHostLifecycle
{
    private static readonly TimeSpan ReminderTickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly IAppHostDatabase _database;
    private readonly IAppHostReminderService _reminderService;
    private readonly IAppHostTimerFactory _timerFactory;
    private readonly IAppHostErrorReporter _errorReporter;
    private readonly TimeSpan _stopTimeout;
    private IAppHostPresentationGateway? _presentationGateway;
    private IAppHostRemoteSync? _remoteSync;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _reminderTickGate = new(1, 1);
    private readonly CancellationTokenSource _hostStopSource = new();
    private readonly object _operationSync = new();
    private TaskCompletionSource<bool> _operationsDrained = CompletedSource();
    private TaskCompletionSource<bool>? _startCompletion;
    private Task? _hostCancellationTask;
    private Task? _schedulerTask;
    private Task? _cleanupTask;
    private int _activeOperations;
    private int _started;
    private int _stopRequested;
    private int _databaseDisposed;

    public AppHost(IServiceProvider services)
        : this(services, AppPaths.ForCurrentUser())
    {
    }

    public AppHost(IServiceProvider services, AppPaths paths)
        : this(
            paths,
            new DatabaseHostService(
                services?.GetService<Database>()
                    ?? throw new InvalidOperationException("Database is not registered.")),
            new ReminderHostService(
                services.GetService<ReminderEngine>()
                    ?? throw new InvalidOperationException("ReminderEngine is not registered.")),
            errorReporter: ResolveErrorReporter(services))
    {
    }

    public AppHost(
        AppPaths paths,
        Database database,
        ReminderEngine reminderEngine)
        : this(
            paths,
            new DatabaseHostService(database),
            new ReminderHostService(reminderEngine))
    {
    }

    public AppHost(
        AppPaths paths,
        IAppHostDatabase database,
        IAppHostReminderService reminderService,
        IAppHostTimerFactory? timerFactory = null,
        IAppHostErrorReporter? errorReporter = null,
        TimeSpan? stopTimeout = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _reminderService = reminderService ?? throw new ArgumentNullException(nameof(reminderService));
        _timerFactory = timerFactory ?? new PeriodicAppHostTimerFactory();
        _errorReporter = errorReporter ?? new DiagnosticAppHostErrorReporter();
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }
    }

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>
    /// Attaches the presentation gateway this host will start, tick, and
    /// dispose. Optional: production composes the gateway after this host is
    /// constructed (it depends on objects that do not exist yet at that
    /// point), so attachment happens separately rather than through the
    /// constructor. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    public void AttachPresentationGateway(IAppHostPresentationGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (Volatile.Read(ref _started) != 0)
        {
            throw new InvalidOperationException(
                "The presentation gateway must be attached before the host starts.");
        }

        _presentationGateway = gateway;
    }

    /// <summary>
    /// Attaches the relay sync service this host will start (after the
    /// reminder scheduler) and dispose during shutdown cleanup. Optional:
    /// production attaches it only when a relay base URL is configured.
    /// Must be called before <see cref="StartAsync"/>.
    /// </summary>
    public void AttachRemoteSync(IAppHostRemoteSync remoteSync)
    {
        ArgumentNullException.ThrowIfNull(remoteSync);
        if (Volatile.Read(ref _started) != 0)
        {
            throw new InvalidOperationException(
                "The remote sync service must be attached before the host starts.");
        }

        _remoteSync = remoteSync;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task startTask;
        TaskCompletionSource<bool>? startCompletionForRunner = null;
        lock (_operationSync)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                startTask = Task.FromException(
                    new ObjectDisposedException(nameof(AppHost)));
            }
            else if (Volatile.Read(ref _started) != 0)
            {
                startTask = Task.CompletedTask;
            }
            else
            {
                if (_startCompletion is null)
                {
                    _startCompletion = NewCompletionSource();
                    startCompletionForRunner = _startCompletion;
                }

                startTask = _startCompletion.Task;
            }
        }

        if (startCompletionForRunner is not null)
        {
            _ = RunStartAsync(startCompletionForRunner);
        }

        return cancellationToken.CanBeCanceled
            ? startTask.WaitAsync(cancellationToken)
            : startTask;
    }

    private async Task RunStartAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            var operation = await BeginOperationAsync(
                requiresStarted: false,
                CancellationToken.None);
            if (operation is null)
            {
                throw new ObjectDisposedException(nameof(AppHost));
            }

            await using (operation)
            {
                CreateDirectories();
                await _database.InitializeAsync(operation.CancellationToken);
                await StartPresentationGatewayAsync(operation.CancellationToken);
                await RunReminderTickAsync(operation.CancellationToken);

                if (!await TryAcquireLifecycleGateAsync(_stopTimeout, operation.CancellationToken))
                {
                    throw new TimeoutException("The application host lifecycle gate could not be acquired.");
                }

                try
                {
                    if (Volatile.Read(ref _stopRequested) != 0
                        || operation.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(operation.CancellationToken);
                    }

                    Volatile.Write(ref _started, 1);
                    _schedulerTask = RunReminderSchedulerAsync(_hostStopSource.Token);
                    await StartRemoteSyncAsync(_hostStopSource.Token);
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }

            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            lock (_operationSync)
            {
                if (ReferenceEquals(_startCompletion, completion))
                {
                    _startCompletion = null;
                }
            }
        }
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        RunResumeTickAsync(cancellationToken);

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(cancellationToken);

    public Task HandleResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Shutdown owns cancellation. A caller's cancellation token must not
        // leave the host half-stopped or allow gate acquisition to wait forever.
        Interlocked.Exchange(ref _stopRequested, 1);
        RequestHostCancellation();
        var deadline = DateTimeOffset.UtcNow + _stopTimeout;

        Task? schedulerTask = Volatile.Read(ref _schedulerTask);
        if (await TryAcquireLifecycleGateAsync(Remaining(deadline), CancellationToken.None))
        {
            try
            {
                Volatile.Write(ref _started, 0);
                schedulerTask = _schedulerTask;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        var operations = GetOperationsDrainedTask();
        var cleanup = EnsureCleanupScheduled(
            schedulerTask,
            operations,
            GetHostCancellationTask());
        try
        {
            await cleanup.WaitAsync(Remaining(deadline));
        }
        catch (TimeoutException)
        {
            // Cleanup remains scheduled and will dispose the database after all
            // in-flight work has stopped. Shared gates are intentionally kept
            // alive so a late operation cannot touch disposed synchronization.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task RunResumeTickAsync(CancellationToken cancellationToken)
    {
        var operation = await BeginOperationAsync(
            requiresStarted: true,
            cancellationToken);
        if (operation is null)
        {
            return;
        }

        await using (operation)
        {
            await RunReminderTickAsync(operation.CancellationToken);
        }
    }

    private async Task RunReminderSchedulerAsync(CancellationToken cancellationToken)
    {
        await using var timer = _timerFactory.Create(ReminderTickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RunReminderTickAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ReportError("reminder-scheduler", exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError("reminder-scheduler", exception);
        }
    }

    private async Task RunReminderTickAsync(CancellationToken cancellationToken)
    {
        await _reminderTickGate.WaitAsync(cancellationToken);
        try
        {
            await _reminderService.TickAsync(cancellationToken);
            await TickPresentationGatewayAsync(cancellationToken);
        }
        finally
        {
            _reminderTickGate.Release();
        }
    }

    private async Task StartPresentationGatewayAsync(CancellationToken cancellationToken)
    {
        var gateway = _presentationGateway;
        if (gateway is null)
        {
            return;
        }

        try
        {
            await gateway.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError("presentation-gateway-start", exception);
        }
    }

    private async Task StartRemoteSyncAsync(CancellationToken cancellationToken)
    {
        var remoteSync = _remoteSync;
        if (remoteSync is null)
        {
            return;
        }

        try
        {
            await remoteSync.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError("remote-sync-start", exception);
        }
    }

    private async Task TickPresentationGatewayAsync(CancellationToken cancellationToken)
    {
        var gateway = _presentationGateway;
        if (gateway is null)
        {
            return;
        }

        try
        {
            await gateway.TickAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError("presentation-gateway-tick", exception);
        }
    }

    private async Task DisposePresentationGatewayAsync()
    {
        var gateway = _presentationGateway;
        if (gateway is null)
        {
            return;
        }

        try
        {
            await gateway.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportError("presentation-gateway-shutdown", exception);
        }
    }

    private async Task DisposeRemoteSyncAsync()
    {
        var remoteSync = _remoteSync;
        if (remoteSync is null)
        {
            return;
        }

        try
        {
            await remoteSync.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportError("remote-sync-stop", exception);
        }
    }

    private async Task<HostOperation?> BeginOperationAsync(
        bool requiresStarted,
        CancellationToken callerCancellationToken)
    {
        if (!await TryAcquireLifecycleGateAsync(_stopTimeout, callerCancellationToken))
        {
            throw new TimeoutException("The application host lifecycle gate could not be acquired.");
        }

        try
        {
            if (Volatile.Read(ref _stopRequested) != 0
                || (requiresStarted && Volatile.Read(ref _started) == 0))
            {
                return null;
            }

            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _hostStopSource.Token,
                callerCancellationToken);
            BeginOperation();
            return new HostOperation(this, linkedCancellation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> TryAcquireLifecycleGateAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        return await _lifecycleGate.WaitAsync(timeout, cancellationToken);
    }

    private void BeginOperation()
    {
        lock (_operationSync)
        {
            if (_activeOperations++ == 0)
            {
                _operationsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndOperation()
    {
        lock (_operationSync)
        {
            if (--_activeOperations == 0)
            {
                _operationsDrained.TrySetResult(true);
            }
        }
    }

    private Task GetOperationsDrainedTask()
    {
        lock (_operationSync)
        {
            return _activeOperations == 0
                ? Task.CompletedTask
                : _operationsDrained.Task;
        }
    }

    private Task EnsureCleanupScheduled(
        Task? schedulerTask,
        Task operationsDrained,
        Task hostCancellationTask)
    {
        lock (_operationSync)
        {
            return _cleanupTask ??= CleanupAsync(
                schedulerTask,
                operationsDrained,
                hostCancellationTask);
        }
    }

    private async Task CleanupAsync(
        Task? schedulerTask,
        Task operationsDrained,
        Task hostCancellationTask)
    {
        if (schedulerTask is not null)
        {
            try
            {
                await schedulerTask;
            }
            catch (Exception exception)
            {
                ReportError("reminder-scheduler-shutdown", exception);
            }
        }

        await operationsDrained;
        await ObserveHostCancellationAsync(hostCancellationTask);
        await DisposePresentationGatewayAsync();
        await DisposeRemoteSyncAsync();
        if (Interlocked.Exchange(ref _databaseDisposed, 1) == 0)
        {
            await _database.DisposeAsync();
        }
    }

    private void CreateDirectories()
    {
        Directory.CreateDirectory(_paths.Root);
        Directory.CreateDirectory(_paths.Backups);
        Directory.CreateDirectory(_paths.Secrets);
        Directory.CreateDirectory(_paths.Logs);
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private static TaskCompletionSource<bool> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RequestHostCancellation()
    {
        Exception? cancellationException = null;
        lock (_operationSync)
        {
            if (_hostCancellationTask is not null)
            {
                return;
            }

            try
            {
                _hostCancellationTask = _hostStopSource.CancelAsync();
            }
            catch (Exception exception)
            {
                cancellationException = exception;
                _hostCancellationTask = Task.CompletedTask;
            }
        }

        if (cancellationException is not null)
        {
            ReportError("host-shutdown-cancellation", cancellationException);
        }

        _ = ObserveHostCancellationAsync(_hostCancellationTask);
    }

    private Task GetHostCancellationTask()
    {
        lock (_operationSync)
        {
            return _hostCancellationTask ?? Task.CompletedTask;
        }
    }

    private async Task ObserveHostCancellationAsync(Task? cancellationTask)
    {
        if (cancellationTask is null)
        {
            return;
        }

        try
        {
            await cancellationTask;
        }
        catch (Exception exception)
        {
            ReportError("host-shutdown-cancellation", exception);
        }
    }

    private void ReportError(string operation, Exception exception)
    {
        try
        {
            _errorReporter.Report(operation, exception);
        }
        catch (Exception reporterException)
        {
            Trace.TraceError(
                "Dudu AppHost error reporter failed for {0}: {1}",
                operation,
                reporterException);
        }
    }

    private static IAppHostErrorReporter ResolveErrorReporter(IServiceProvider services) =>
        services.GetService<IAppHostErrorReporter>()
            ?? new DiagnosticAppHostErrorReporter(
                services.GetService<ILoggerFactory>()?.CreateLogger("Dudu.AppHost"));

    private sealed class HostOperation(
        AppHost owner,
        CancellationTokenSource linkedCancellation) : IAsyncDisposable
    {
        private int _disposed;

        public CancellationToken CancellationToken => linkedCancellation.Token;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.ObserveHostCancellationAsync(owner.GetHostCancellationTask());
                linkedCancellation.Dispose();
                owner.EndOperation();
            }

        }
    }

    private sealed class DatabaseHostService(Database database) : IAppHostDatabase
    {
        private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            _database.InitializeAsync(cancellationToken);

        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }

    private sealed class ReminderHostService(ReminderEngine reminderEngine) : IAppHostReminderService
    {
        private readonly ReminderEngine _reminderEngine = reminderEngine
            ?? throw new ArgumentNullException(nameof(reminderEngine));

        public Task TickAsync(CancellationToken cancellationToken = default) =>
            _reminderEngine.TickAsync(cancellationToken);
    }

    private sealed class PeriodicAppHostTimerFactory : IAppHostTimerFactory
    {
        public IAppHostTimer Create(TimeSpan interval) => new PeriodicAppHostTimer(interval);
    }

    private sealed class PeriodicAppHostTimer(TimeSpan interval) : IAppHostTimer
    {
        private readonly PeriodicTimer _timer = new(interval);

        public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default) =>
            _timer.WaitForNextTickAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _timer.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DiagnosticAppHostErrorReporter(ILogger? logger = null) : IAppHostErrorReporter
    {
        public void Report(string operation, Exception exception)
        {
            if (logger is not null)
            {
                logger.LogError(
                    exception,
                    "Dudu AppHost operation {Operation} failed.",
                    operation);
                return;
            }

            Trace.TraceError(
                "Dudu AppHost operation {0} failed: {1}",
                operation,
                exception);
            Console.Error.WriteLine($"Dudu AppHost operation '{operation}' failed: {exception}");
        }
    }
}
