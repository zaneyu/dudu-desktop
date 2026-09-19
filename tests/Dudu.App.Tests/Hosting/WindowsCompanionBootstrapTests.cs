using System.Threading.Channels;
using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class WindowsCompanionBootstrapTests
{
    [Fact]
    public async Task Secondary_activates_primary_before_factory_and_runtime_start()
    {
        var shared = new SharedTransportState();
        var primaryRuntime = new FakeRuntime();
        var primaryFactoryCalls = 0;
        var secondaryFactoryCalls = 0;
        await using var primary = new WindowsCompanionBootstrap(
            _ =>
            {
                Interlocked.Increment(ref primaryFactoryCalls);
                return Task.FromResult<IPrimaryAppRuntime>(primaryRuntime);
            },
            new FakeTransport(shared));
        await using var secondary = new WindowsCompanionBootstrap(
            _ =>
            {
                Interlocked.Increment(ref secondaryFactoryCalls);
                return Task.FromResult<IPrimaryAppRuntime>(new FakeRuntime());
            },
            new FakeTransport(shared));

        Assert.True(await primary.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(await secondary.StartAsync(TestContext.Current.CancellationToken));
        await primaryRuntime.Activation.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, primaryFactoryCalls);
        Assert.Equal(0, secondaryFactoryCalls);
        Assert.Equal(1, primaryRuntime.Activations);
        Assert.Equal(1, primaryRuntime.Starts);
    }

    [Fact]
    public async Task Fast_secondary_activation_is_queued_until_runtime_is_ready_once()
    {
        var shared = new SharedTransportState();
        var factoryEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        await using var primary = new WindowsCompanionBootstrap(
            async cancellationToken =>
            {
                factoryEntered.TrySetResult(true);
                await releaseFactory.Task.WaitAsync(cancellationToken);
                return runtime;
            },
            new FakeTransport(shared));
        await using var secondary = new WindowsCompanionBootstrap(
            _ => throw new Xunit.Sdk.XunitException("Secondary created a runtime."),
            new FakeTransport(shared));

        var primaryStart = primary.StartAsync(TestContext.Current.CancellationToken);
        await factoryEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(await secondary.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(runtime.Activation.Task.IsCompleted);

        releaseFactory.TrySetResult(true);
        Assert.True(await primaryStart);
        await runtime.Activation.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Activations);
    }

    [Fact]
    public async Task Startup_runner_observes_factory_failure_and_exits_once()
    {
        var reported = 0;
        var boxShown = 0;
        var exits = 0;
        var runner = new CompanionStartupRunner(
            (_, _) => Task.FromException<WindowsCompanionBootstrap>(
                new InvalidOperationException("factory failed")),
            _ => reported++,
            () => boxShown++,
            () => exits++);

        await runner.RunAsync("--background", TestContext.Current.CancellationToken);

        Assert.Equal(1, reported);
        Assert.Equal(1, boxShown);
        Assert.Equal(1, exits);
        Assert.Null(runner.Bootstrap);
    }

    [Fact]
    public async Task Startup_runner_disposes_partial_bootstrap_when_start_fails()
    {
        var shared = new SharedTransportState();
        var runtime = new FakeRuntime { StartFailure = new InvalidOperationException("start failed") };
        var reported = 0;
        var boxShown = 0;
        var exits = 0;
        var runner = new CompanionStartupRunner(
            (_, _) => Task.FromResult(new WindowsCompanionBootstrap(
                _ => Task.FromResult<IPrimaryAppRuntime>(runtime),
                new FakeTransport(shared))),
            _ => reported++,
            () => boxShown++,
            () => exits++);

        await runner.RunAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(1, reported);
        Assert.Equal(1, boxShown);
        Assert.Equal(1, exits);
        Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public async Task Startup_runner_disposes_the_bootstrap_before_showing_the_failure_box()
    {
        // The dispose releases the single-instance mutex/pipe; showing the
        // box first used to leave them held while the modal was up, turning
        // her next manual launch into a secondary that silently no-ops.
        var shared = new SharedTransportState();
        var runtime = new FakeRuntime { StartFailure = new InvalidOperationException("start failed") };
        var order = new List<string>();
        var runner = new CompanionStartupRunner(
            (_, _) => Task.FromResult(new WindowsCompanionBootstrap(
                _ => Task.FromResult<IPrimaryAppRuntime>(runtime),
                new FakeTransport(shared, order))),
            _ => { },
            () => order.Add("box"),
            () => { });

        await runner.RunAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(["transport-disposed", "box"], order);
    }

    [Fact]
    public async Task Concurrent_start_and_dispose_are_single_flight()
    {
        var shared = new SharedTransportState();
        var factoryEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        await using var bootstrap = new WindowsCompanionBootstrap(
            async cancellationToken =>
            {
                factoryEntered.TrySetResult(true);
                await releaseFactory.Task.WaitAsync(cancellationToken);
                return runtime;
            },
            new FakeTransport(shared));

        var firstStart = bootstrap.StartAsync(TestContext.Current.CancellationToken);
        var secondStart = bootstrap.StartAsync(TestContext.Current.CancellationToken);
        await factoryEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var firstDispose = bootstrap.DisposeAsync().AsTask();
        var secondDispose = bootstrap.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);

        releaseFactory.TrySetResult(true);
        Assert.True(await firstStart);
        Assert.True(await secondStart);
        await Task.WhenAll(firstDispose, secondDispose);

        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public void Tray_attach_failure_on_a_hidden_launch_also_opens_home_before_the_forced_show()
    {
        // Regression: forceVisible alone does not guarantee an exit surface
        // for a hidden (--background) launch whose tray attach also failed.
        // The forced overlay show still goes through SetUserVisibleAsync's
        // normal TryCanShow gate (quiet hours/pause), which can veto it,
        // leaving neither a tray icon nor a visible overlay. Opening
        // Settings directly (not through OnHotkeyAsync, which is itself
        // gated by TryCanShow) guarantees a way to reach and quit the app
        // regardless of quiet hours or a pause. This can't run on Mac
        // (StartCoreAsync needs real Win32 window/tray/hotkey handles), so
        // the ordering is asserted from source the same way
        // ProductionStartupContractTests does for other native-only paths.
        var root = FindRepositoryRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionBootstrap.cs"));

        var forceVisibleSet = runtime.IndexOf("forceVisible = true;", StringComparison.Ordinal);
        var openHomeFallback = runtime.IndexOf("await _openHome(cancellationToken);", StringComparison.Ordinal);
        var visibilityGate = runtime.IndexOf(
            "await StartupVisibilityGate.ApplyAsync(",
            StringComparison.Ordinal);

        Assert.True(forceVisibleSet >= 0);
        Assert.True(openHomeFallback > forceVisibleSet);
        Assert.True(visibilityGate > openHomeFallback);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output path.");
    }

    private sealed class FakeRuntime : IPrimaryAppRuntime
    {
        public TaskCompletionSource<bool> Activation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int Activations { get; private set; }
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public Exception? StartFailure { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            if (StartFailure is not null) return Task.FromException(StartFailure);
            return Task.CompletedTask;
        }

        public Task ActivateAsync(AppActivation activation, CancellationToken cancellationToken = default)
        {
            Assert.Equal(AppActivation.OpenHome, activation);
            Activations++;
            Activation.TrySetResult(true);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SharedTransportState
    {
        public int Primary;
        public Channel<byte[]> Payloads { get; } = Channel.CreateUnbounded<byte[]>();
    }

    private sealed class FakeTransport(SharedTransportState state, List<string>? disposeOrder = null) : IActivationTransport
    {
        private bool _ownsPrimary;

        public Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ownsPrimary = Interlocked.CompareExchange(ref state.Primary, 1, 0) == 0;
            return Task.FromResult(_ownsPrimary);
        }

        public async Task ListenAsync(
            Func<ReadOnlyMemory<byte>, Task> onPayload,
            CancellationToken cancellationToken)
        {
            await foreach (var payload in state.Payloads.Reader.ReadAllAsync(cancellationToken))
            {
                await onPayload(payload);
            }
        }

        public Task SendAsync(byte payload, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return state.Payloads.Writer.WriteAsync([payload], cancellationToken).AsTask();
        }

        public ValueTask DisposeAsync()
        {
            if (_ownsPrimary)
            {
                Interlocked.Exchange(ref state.Primary, 0);
            }

            disposeOrder?.Add("transport-disposed");
            return ValueTask.CompletedTask;
        }
    }
}
