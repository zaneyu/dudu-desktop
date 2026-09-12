using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class StartupVisibilityGateTests
{
    [Fact]
    public async Task Initial_visibility_waits_until_fullscreen_sampling_completes()
    {
        var samplingStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSampling = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var visibilityRequested = false;
        var cancellationToken = TestContext.Current.CancellationToken;

        var apply = StartupVisibilityGate.ApplyAsync(
            async token =>
            {
                samplingStarted.TrySetResult(true);
                await releaseSampling.Task.WaitAsync(token);
            },
            (visible, _) =>
            {
                visibilityRequested = visible;
                return Task.CompletedTask;
            },
            desiredVisible: true,
            cancellationToken);

        await samplingStarted.Task.WaitAsync(cancellationToken);
        Assert.False(visibilityRequested);

        releaseSampling.TrySetResult(true);
        await apply;

        Assert.True(visibilityRequested);
    }

    [Fact]
    public async Task Failed_fullscreen_sampling_never_requests_visibility()
    {
        var visibilityCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupVisibilityGate.ApplyAsync(
                _ => Task.FromException(new InvalidOperationException("sample failed")),
                (_, _) =>
                {
                    visibilityCalls++;
                    return Task.CompletedTask;
                },
                desiredVisible: true,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, visibilityCalls);
    }
}
