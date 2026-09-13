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
        fixture.Database.InitializationGate = NewSource();

        var firstStart = fixture.Host.StartAsync(CancellationToken.None);
        await fixture.Database.InitializationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var concurrentStart = fixture.Host.StartAsync(CancellationToken.None);

        Assert.Same(firstStart, concurrentStart);
        fixture.Database.InitializationGate.TrySetResult(true);
        await Task.WhenAll(firstStart, concurrentStart);
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Database.InitializeCount);
        Assert.Equal(1, fixture.Reminder.TickCount);
        Assert.Equal(1, fixture.TimerFactory.CreateCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_waits_for_migration_before_first_tick_and_starts_scheduler()
    {
        using var fixture = new AppHostFixture();

        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Database.Initialized);
        Assert.Equal(1, fixture.Reminder.TickCount);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.TimerFactory.Interval);
        Assert.Equal(1, fixture.TimerFactory.CreateCount);

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task Failed_migration_does_not_start_reminder_scheduler()
    {
        using var fixture = new AppHostFixture();
        fixture.Database.InitializationException = new InvalidOperationException("migration failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.Reminder.TickCount);
        Assert.Equal(0, fixture.TimerFactory.CreateCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_runs_one_additional_reminder_tick()
    {
        using var fixture = new AppHostFixture();
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        await fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Reminder.TickCount);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Scheduler_uses_thirty_second_timer_and_reports_tick_failures()
    {
        using var fixture = new AppHostFixture();
        fixture.Reminder.ThrowOnScheduledTick = true;
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        fixture.TimerFactory.Timer.Signal();
        await fixture.Errors.Reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("reminder-scheduler", fixture.Errors.LastOperation);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.TimerFactory.Interval);
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_is_bounded_and_does_not_dispose_database_while_resume_is_live()
    {
        using var fixture = new AppHostFixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        fixture.Reminder.BlockNextTick = true;

        var resumeTask = fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);
        await fixture.Reminder.BlockEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(fixture.Database.Disposed);

        fixture.Reminder.ReleaseBlockedTick();
        await resumeTask;
        await fixture.Database.DisposedSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Database.Disposed);
    }

    [Fact]
    public async Task Shutdown_is_bounded_when_cancellation_callback_is_slow_and_throws()
    {
        using var fixture = new AppHostFixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        fixture.Reminder.BlockNextTick = true;
        fixture.Reminder.CancellationCallback = () =>
        {
            fixture.Reminder.CancellationCallbackEntered.TrySetResult(true);
            Thread.Sleep(300);
            throw new InvalidOperationException("cancellation callback failed");
        };

        var resumeTask = fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);
        await fixture.Reminder.BlockEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(fixture.Database.Disposed);

        fixture.Reminder.ReleaseBlockedTick();
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
    public async Task Presentation_gateway_is_started_after_db_init_then_ticked_per_reminder_tick_then_disposed()
    {
        using var fixture = new AppHostFixture();
        var gateway = new TestPresentationGateway(
            () => fixture.Database.Initialized,
            () => fixture.Reminder.TickCount);
        fixture.Host.AttachPresentationGateway(gateway);

        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

        // StartAsync must run after the database has finished initializing
        // and before the first reminder tick has happened.
        Assert.Equal(1, gateway.StartCount);
        Assert.True(gateway.DatabaseInitializedAtStart);
        Assert.Equal(0, gateway.ReminderTickCountAtStart);

        // TickAsync must only run after a successful reminder tick: by the
        // time it is first invoked, the reminder service's tick count has
        // already advanced.
        Assert.Equal(1, gateway.TickCount);
        Assert.Equal(1, gateway.ReminderTickCountAtFirstGatewayTick);
        Assert.Equal(0, gateway.DisposeCount);

        await fixture.Host.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, gateway.TickCount);
        Assert.Equal(2, gateway.ReminderTickCountAtLastGatewayTick);
        Assert.Equal(0, gateway.DisposeCount);

        await fixture.Host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gateway.DisposeCount);
    }

    private sealed class TestPresentationGateway(
        Func<bool>? databaseInitialized = null,
        Func<int>? reminderTickCount = null) : IAppHostPresentationGateway
    {
        public int StartCount { get; private set; }
        public int TickCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool DatabaseInitializedAtStart { get; private set; }
        public int ReminderTickCountAtStart { get; private set; }
        public int ReminderTickCountAtFirstGatewayTick { get; private set; } = -1;
        public int ReminderTickCountAtLastGatewayTick { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            DatabaseInitializedAtStart = databaseInitialized?.Invoke() ?? false;
            ReminderTickCountAtStart = reminderTickCount?.Invoke() ?? 0;
            return Task.CompletedTask;
        }

        public Task TickAsync(CancellationToken cancellationToken = default)
        {
            TickCount++;
            var count = reminderTickCount?.Invoke() ?? 0;
            if (ReminderTickCountAtFirstGatewayTick < 0)
            {
                ReminderTickCountAtFirstGatewayTick = count;
            }

            ReminderTickCountAtLastGatewayTick = count;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
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
            Reminder = new TestReminderService();
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
                Reminder,
                TimerFactory,
                Errors,
                stopTimeout);
        }

        public TestDatabase Database { get; }
        public TestReminderService Reminder { get; }
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

    private sealed class TestReminderService : IAppHostReminderService
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _blockedTick;

        public bool BlockNextTick { get; set; }
        public bool ThrowOnScheduledTick { get; set; }
        public Action? CancellationCallback { get; set; }
        public int TickCount { get; private set; }
        public TaskCompletionSource<bool> BlockEntered { get; } = NewSource();
        public TaskCompletionSource<bool> CancellationCallbackEntered { get; } = NewSource();

        public async Task TickAsync(CancellationToken cancellationToken = default)
        {
            TickCount++;
            if (ThrowOnScheduledTick && TickCount > 1)
            {
                throw new InvalidOperationException("scheduled tick failed");
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
        }

        public void ReleaseBlockedTick()
        {
            lock (_sync)
            {
                _blockedTick?.TrySetResult(true);
            }
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
