using System.Diagnostics;
using Dudu.App.Hosting;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

public sealed class AppHostTests
{
    [Fact]
    public async Task Concurrent_and_repeated_starts_are_single_flight_and_idempotent()
    {
        using var fixture = new AppHostFixture();
        var gateway = new TestPresentationGateway();
        fixture.Host.AttachPresentationGateway(gateway);
        fixture.Database.InitializationGate = NewSource();

        var firstStart = fixture.Host.StartAsync(CancellationToken.None);
        await fixture.Database.InitializationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var concurrentStart = fixture.Host.StartAsync(CancellationToken.None);

        Assert.Same(firstStart, concurrentStart);
        fixture.Database.InitializationGate.TrySetResult(true);
        await Task.WhenAll(firstStart, concurrentStart);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Database.InitializeCount);
        Assert.Equal(1, gateway.StartCount);
        Assert.Equal(1, fixture.TimerFactory.CreateCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_waits_for_migration_and_starts_the_thirty_second_scheduler()
    {
        using var fixture = new AppHostFixture();

        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Database.Initialized);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.TimerFactory.Interval);
        Assert.Equal(1, fixture.TimerFactory.CreateCount);

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task Failed_migration_does_not_start_the_gateway_or_the_scheduler()
    {
        using var fixture = new AppHostFixture();
        var calls = new List<string>();
        fixture.Host.AttachPresentationGateway(new TestPresentationGateway(calls));
        fixture.Database.InitializationException = new InvalidOperationException("migration failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Empty(calls);
        Assert.Equal(0, fixture.TimerFactory.CreateCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_starts_the_gateway_without_reconciling_or_releasing()
    {
        // Startup runs before WindowsCompanionBootstrap pushes the real
        // fullscreen/lock/visibility state, so it must only start the
        // gateway (reloading held rows) -- never reconcile visibility or
        // release a held presentation. The first timer tick does both.
        using var fixture = new AppHostFixture();
        var calls = new List<string>();
        fixture.Host.AttachPresentationGateway(new TestPresentationGateway(calls));
        fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));

        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["start"], calls);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gateway_is_started_after_db_init_and_disposed_on_stop()
    {
        using var fixture = new AppHostFixture();
        var gateway = new TestPresentationGateway(databaseInitialized: () => fixture.Database.Initialized);
        fixture.Host.AttachPresentationGateway(gateway);

        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gateway.StartCount);
        Assert.True(gateway.DatabaseInitializedAtStart);
        Assert.Equal(0, gateway.TickCount);
        Assert.Equal(0, gateway.DisposeCount);

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gateway.DisposeCount);
    }

    [Fact]
    public async Task Timer_signal_reconciles_then_ticks_the_gateway()
    {
        using var fixture = new AppHostFixture();
        var calls = new List<string>();
        var gateway = new TestPresentationGateway(calls);
        fixture.Host.AttachPresentationGateway(gateway);
        fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        fixture.TimerFactory.Timer.Signal();
        await gateway.Ticked.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["start", "reconcile", "tick"], gateway.SnapshotCalls());
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timer_ticks_the_gateway_with_no_reconciler_attached()
    {
        using var fixture = new AppHostFixture();
        var gateway = new TestPresentationGateway();
        fixture.Host.AttachPresentationGateway(gateway);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        fixture.TimerFactory.Timer.Signal();
        await gateway.Ticked.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gateway.TickCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_reconciles_then_ticks_the_gateway()
    {
        using var fixture = new AppHostFixture();
        var calls = new List<string>();
        fixture.Host.AttachPresentationGateway(new TestPresentationGateway(calls));
        fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        await fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["start", "reconcile", "tick"], calls);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_before_start_does_nothing()
    {
        using var fixture = new AppHostFixture();
        var calls = new List<string>();
        fixture.Host.AttachPresentationGateway(new TestPresentationGateway(calls));
        fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));

        await fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task Throwing_gateway_tick_is_reported_and_the_scheduler_keeps_ticking()
    {
        using var fixture = new AppHostFixture();
        var gateway = new TestPresentationGateway { ThrowOnTick = true };
        fixture.Host.AttachPresentationGateway(gateway);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        fixture.TimerFactory.Timer.Signal();
        await fixture.Errors.WaitForOperationAsync(
            "presentation-gateway-tick",
            TestContext.Current.CancellationToken);

        gateway.ThrowOnTick = false;
        fixture.TimerFactory.Timer.Signal();
        await gateway.Ticked.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, gateway.TickCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Scheduler_failure_is_reported_as_presentation_scheduler()
    {
        using var fixture = new AppHostFixture();
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        fixture.TimerFactory.Timer.Fail(new InvalidOperationException("timer failed"));
        await fixture.Errors.Reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("presentation-scheduler", fixture.Errors.LastOperation);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.TimerFactory.Interval);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_is_bounded_and_does_not_dispose_database_while_resume_is_live()
    {
        using var fixture = new AppHostFixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        var gateway = new TestPresentationGateway();
        fixture.Host.AttachPresentationGateway(gateway);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        gateway.BlockNextTick = true;

        var resumeTask = fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);
        await gateway.BlockEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(fixture.Database.Disposed);

        gateway.ReleaseBlockedTick();
        await resumeTask;
        await fixture.Database.DisposedSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task Shutdown_is_bounded_when_cancellation_callback_is_slow_and_throws()
    {
        using var fixture = new AppHostFixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        var gateway = new TestPresentationGateway();
        fixture.Host.AttachPresentationGateway(gateway);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        gateway.BlockNextTick = true;
        gateway.CancellationCallback = () =>
        {
            gateway.CancellationCallbackEntered.TrySetResult(true);
            Thread.Sleep(300);
            throw new InvalidOperationException("cancellation callback failed");
        };

        var resumeTask = fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);
        await gateway.BlockEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(fixture.Database.Disposed);

        gateway.ReleaseBlockedTick();
        await resumeTask;
        await fixture.Database.DisposedSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.Errors.WaitForOperationAsync(
            "host-shutdown-cancellation",
            TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task Shutdown_waits_for_underlying_database_initialization_before_disposal()
    {
        using var fixture = new AppHostFixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        fixture.Database.InitializationGate = NewSource();
        var startTask = fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        await fixture.Database.InitializationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Database.Disposed);
        fixture.Database.InitializationGate.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        await fixture.Database.DisposedSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task AttachPresentationGateway_throws_once_the_host_has_started()
    {
        using var fixture = new AppHostFixture();
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(
            () => fixture.Host.AttachPresentationGateway(new TestPresentationGateway()));

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Remote_sync_starts_only_when_attached_and_is_disposed_on_stop()
    {
        // Relay configured: the fake stands in for a RemoteSyncService wrapper;
        // AppHost starts it after the presentation scheduler and disposes it on stop.
        using var configured = new AppHostFixture();
        var remoteSync = new TestRemoteSync(() => configured.TimerFactory.CreateCount);
        configured.Host.AttachRemoteSync(remoteSync);

        await configured.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, remoteSync.StartCount);
        Assert.Equal(1, remoteSync.SchedulerCreateCountAtStart);
        Assert.Equal(0, remoteSync.DisposeCount);

        await configured.Host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, remoteSync.DisposeCount);

        // No relay configured (a null DUDU_RELAY_BASE_URL / ProductInfo.DefaultRelayBaseUrl):
        // nothing is attached, and the host starts and stops exactly as before.
        using var unconfigured = new AppHostFixture();

        await unconfigured.Host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(unconfigured.Database.Initialized);
        await unconfigured.Host.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(unconfigured.Database.Disposed);
    }

    private sealed class TestRemoteSync(Func<int>? schedulerCreateCount = null) : IAppHostRemoteSync
    {
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SchedulerCreateCountAtStart { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            SchedulerCreateCountAtStart = schedulerCreateCount?.Invoke() ?? 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPresentationGateway(
        List<string>? calls = null,
        Func<bool>? databaseInitialized = null) : IAppHostPresentationGateway
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _blockedTick;

        public int StartCount { get; private set; }
        public int TickCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool DatabaseInitializedAtStart { get; private set; }
        public bool ThrowOnTick { get; set; }
        public bool BlockNextTick { get; set; }
        public Action? CancellationCallback { get; set; }
        public TaskCompletionSource<bool> BlockEntered { get; } = NewSource();
        public TaskCompletionSource<bool> CancellationCallbackEntered { get; } = NewSource();

        /// <summary>
        /// Lets a test wait deterministically for a tick reached through the
        /// scheduler's own timer signal instead of the synchronous
        /// ResumeAsync path. Completed only by a tick that did not throw.
        /// </summary>
        public TaskCompletionSource<bool> Ticked { get; } = NewSource();

        public string[] SnapshotCalls()
        {
            if (calls is null)
            {
                return [];
            }

            lock (calls)
            {
                return calls.ToArray();
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            DatabaseInitializedAtStart = databaseInitialized?.Invoke() ?? false;
            Record("start");
            return Task.CompletedTask;
        }

        public async Task TickAsync(CancellationToken cancellationToken = default)
        {
            TickCount++;
            Record("tick");
            if (ThrowOnTick)
            {
                throw new InvalidOperationException("gateway tick failed");
            }

            if (BlockNextTick)
            {
                lock (_sync)
                {
                    BlockNextTick = false;
                    _blockedTick = NewSource();
                    if (CancellationCallback is not null)
                    {
                        cancellationToken.Register(CancellationCallback);
                    }
                    BlockEntered.TrySetResult(true);
                }

                await _blockedTick.Task;
            }

            Ticked.TrySetResult(true);
        }

        public void ReleaseBlockedTick()
        {
            lock (_sync)
            {
                _blockedTick?.TrySetResult(true);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private void Record(string call)
        {
            if (calls is null)
            {
                return;
            }

            lock (calls)
            {
                calls.Add(call);
            }
        }
    }

    private sealed class TestVisibilityReconciler(List<string> calls) : IAppHostVisibilityReconciler
    {
        public Task ReconcileVisibilityAsync(CancellationToken cancellationToken = default)
        {
            lock (calls)
            {
                calls.Add("reconcile");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AppHostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "dudu-tests",
            Guid.NewGuid().ToString("N"));

        public AppHostFixture(TimeSpan? stopTimeout = null)
        {
            Directory.CreateDirectory(_root);
            Database = new TestDatabase();
            TimerFactory = new TestTimerFactory();
            Errors = new TestErrorReporter();
            Host = new AppHost(
                new AppPaths(
                    _root,
                    Path.Combine(_root, "dudu.db"),
                    Path.Combine(_root, "backups"),
                    Path.Combine(_root, "secrets"),
                    Path.Combine(_root, "logs")),
                Database,
                TimerFactory,
                Errors,
                stopTimeout);
        }

        public TestDatabase Database { get; }
        public TestTimerFactory TimerFactory { get; }
        public TestErrorReporter Errors { get; }
        public AppHost Host { get; }

        public void Dispose()
        {
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TestDatabase : IAppHostDatabase
    {
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public int InitializeCount { get; private set; }
        public Exception? InitializationException { get; set; }
        public TaskCompletionSource<bool>? InitializationGate { get; set; }
        public TaskCompletionSource<bool> InitializationEntered { get; } = NewSource();
        public TaskCompletionSource<bool> DisposedSignal { get; } = NewSource();

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            InitializationEntered.TrySetResult(true);
            if (InitializationException is not null)
            {
                throw InitializationException;
            }

            if (InitializationGate is not null)
            {
                await InitializationGate.Task;
            }

            Initialized = true;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposedSignal.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTimerFactory : IAppHostTimerFactory
    {
        public TimeSpan Interval { get; private set; }
        public int CreateCount { get; private set; }
        public TestTimer Timer { get; } = new();

        public IAppHostTimer Create(TimeSpan interval)
        {
            Interval = interval;
            CreateCount++;
            return Timer;
        }
    }

    private sealed class TestTimer : IAppHostTimer
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool> _next = NewSource();

        public void Signal()
        {
            lock (_sync)
            {
                _next.TrySetResult(true);
            }
        }

        public void Fail(Exception exception)
        {
            lock (_sync)
            {
                _next.TrySetException(exception);
            }
        }

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
        {
            Task<bool> next;
            lock (_sync)
            {
                next = _next.Task;
            }

            var result = await next.WaitAsync(cancellationToken);
            lock (_sync)
            {
                if (ReferenceEquals(_next.Task, next))
                {
                    _next = NewSource();
                }
            }

            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestErrorReporter : IAppHostErrorReporter
    {
        private readonly object _sync = new();
        private readonly List<string> _operations = [];
        public string? LastOperation { get; private set; }
        public TaskCompletionSource<bool> Reported { get; } = NewSource();

        public void Report(string operation, Exception exception)
        {
            lock (_sync)
            {
                LastOperation = operation;
                _operations.Add(operation);
            }
            Reported.TrySetResult(true);
        }

        public async Task WaitForOperationAsync(string operation, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_operations.Contains(operation, StringComparer.Ordinal))
                    {
                        return;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private static TaskCompletionSource<bool> NewSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
