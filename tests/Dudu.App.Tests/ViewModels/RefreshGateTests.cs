using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class RefreshGateTests
{
    [Fact]
    public async Task Overlapping_refreshes_serialize_and_skip_superseded_requests()
    {
        var viewModel = new GateProbeViewModel();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new List<string>();
        var concurrent = 0;
        var maxConcurrent = 0;

        async Task Body(string name, Task? block)
        {
            maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref concurrent));
            lock (ran) ran.Add(name);
            if (block is not null)
            {
                firstStarted.TrySetResult();
                await block;
            }

            Interlocked.Decrement(ref concurrent);
        }

        var first = viewModel.Refresh(_ => Body("first", releaseFirst.Task));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stale = viewModel.Refresh(_ => Body("stale", null));
        var latest = viewModel.Refresh(_ => Body("latest", null));
        releaseFirst.TrySetResult();

        await Task.WhenAll(first, stale, latest).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "latest"], ran);
        Assert.Equal(1, maxConcurrent);
    }

    private sealed class GateProbeViewModel : FeatureViewModelBase
    {
        public Task Refresh(Func<CancellationToken, Task> body) =>
            RunRefreshAsync(body, CancellationToken.None);
    }
}
