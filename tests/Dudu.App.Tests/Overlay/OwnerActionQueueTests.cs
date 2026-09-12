using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OwnerActionQueueTests
{
    [Fact]
    public async Task Posting_failure_completes_invocation_promptly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var queue = new OwnerActionQueue(
            isOwnerThread: () => false,
            post: () => new InvalidOperationException("post rejected"));

        var task = queue.InvokeAsync(() => { }, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));
        Assert.Equal("post rejected", exception.Message);
    }

    [Fact]
    public async Task Close_fails_queued_capture_and_placement_without_waiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var queue = new OwnerActionQueue(
            isOwnerThread: () => false,
            post: () => null);
        var capture = queue.InvokeAsync(() => "captured", cancellationToken);
        var placement = queue.InvokeAsync(() => { }, cancellationToken);

        queue.Close();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            capture.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            placement.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));
    }

    [Fact]
    public async Task Cancellation_while_queued_completes_as_canceled()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var queue = new OwnerActionQueue(
            isOwnerThread: () => false,
            post: () => null);
        using var cancellation = new CancellationTokenSource();
        var task = queue.InvokeAsync(() => { }, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(1), testCancellationToken));
    }

    [Fact]
    public async Task Dispose_racing_two_owner_calls_completes_both()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var queue = new OwnerActionQueue(
            isOwnerThread: () => false,
            post: () => null);
        var capture = queue.InvokeAsync(() => "captured", cancellationToken);
        var placement = queue.InvokeAsync(() => { }, cancellationToken);

        queue.Dispose();

        var all = Task.WhenAll(
            Observe(capture),
            Observe(placement));
        var results = await all.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.All(results, result => Assert.True(result is "completed" or "rejected"));
    }

    private static async Task<string> Observe(Task task)
    {
        try
        {
            await task;
            return "completed";
        }
        catch (ObjectDisposedException)
        {
            return "rejected";
        }
    }
}
