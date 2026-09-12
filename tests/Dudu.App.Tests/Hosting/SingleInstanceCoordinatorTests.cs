using Dudu.App.Hosting;
using System.Threading.Channels;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task Second_instance_sends_one_validated_activation_and_never_starts_host()
    {
        var transport = new InMemoryActivationTransport();
        await using var primary = new SingleInstanceCoordinator(transport);
        await using var secondary = new SingleInstanceCoordinator(transport);

        Assert.True(await primary.TryAcquireAsync(TestContext.Current.CancellationToken));
        Assert.False(await secondary.TryAcquireAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AppActivation.OpenHome, await transport.NextAsync());
        Assert.Equal(1, transport.SendCount);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public async Task Malformed_and_duplicate_payloads_are_ignored_without_killing_the_listener()
    {
        var transport = new InMemoryActivationTransport();
        var activations = new List<AppActivation>();
        await using var primary = new SingleInstanceCoordinator(
            transport,
            activation =>
            {
                activations.Add(activation);
                return Task.CompletedTask;
            });

        Assert.True(await primary.TryAcquireAsync(TestContext.Current.CancellationToken));
        await transport.InjectAsync([0xFF]);
        await transport.InjectAsync([(byte)AppActivation.OpenHome]);
        await transport.InjectAsync([(byte)AppActivation.OpenHome]);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(2, activations.Count);
    }

    [Fact]
    public async Task Canceled_listener_disposes_and_releases_primary_for_recovery()
    {
        var transport = new InMemoryActivationTransport();
        await using (var primary = new SingleInstanceCoordinator(transport))
        {
            Assert.True(await primary.TryAcquireAsync(TestContext.Current.CancellationToken));
        }

        await using var recovered = new SingleInstanceCoordinator(transport);
        Assert.True(await recovered.TryAcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Handler_failure_does_not_stop_listener_recovery()
    {
        var transport = new InMemoryActivationTransport();
        var calls = 0;
        var secondCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new SingleInstanceCoordinator(
            transport,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("simulated activation failure");
                }

                secondCall.TrySetResult(true);
                return Task.CompletedTask;
            });

        Assert.True(await primary.TryAcquireAsync(TestContext.Current.CancellationToken));
        await transport.InjectAsync([(byte)AppActivation.OpenHome]);
        await transport.InjectAsync([(byte)AppActivation.OpenHome]);

        await secondCall.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    private sealed class InMemoryActivationTransport : IActivationTransport
    {
        private readonly Channel<byte[]> _payloads = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _observed = Channel.CreateUnbounded<byte[]>();
        private int _primary;

        public int SendCount { get; private set; }

        public Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.CompareExchange(ref _primary, 1, 0) == 0);
        }

        public async Task ListenAsync(
            Func<ReadOnlyMemory<byte>, Task> onPayload,
            CancellationToken cancellationToken)
        {
            await foreach (var payload in _payloads.Reader.ReadAllAsync(cancellationToken))
            {
                await onPayload(payload);
            }
        }

        public Task SendAsync(byte payload, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            var bytes = new byte[] { payload };
            _observed.Writer.TryWrite(bytes);
            return _payloads.Writer.WriteAsync(bytes, cancellationToken).AsTask();
        }

        public Task InjectAsync(byte[] payload) => _payloads.Writer.WriteAsync(payload).AsTask();

        public async Task<AppActivation> NextAsync()
        {
            var payload = await _observed.Reader.ReadAsync();
            return (AppActivation)payload[0];
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _primary, 0);
            return ValueTask.CompletedTask;
        }
    }
}
