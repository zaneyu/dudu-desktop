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

    private sealed class FakeRuntime : IPrimaryAppRuntime
    {
        public TaskCompletionSource<bool> Activation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int Activations { get; private set; }
        public int Starts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.CompletedTask;
        }

        public Task ActivateAsync(AppActivation activation, CancellationToken cancellationToken = default)
        {
            Assert.Equal(AppActivation.OpenHome, activation);
            Activations++;
            Activation.TrySetResult(true);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SharedTransportState
    {
        public int Primary;
        public Channel<byte[]> Payloads { get; } = Channel.CreateUnbounded<byte[]>();
    }

    private sealed class FakeTransport(SharedTransportState state) : IActivationTransport
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

            return ValueTask.CompletedTask;
        }
    }
}
