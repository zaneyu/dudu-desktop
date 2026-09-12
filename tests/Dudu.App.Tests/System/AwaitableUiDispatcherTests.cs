using Dudu.App.Hosting;
using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class AwaitableUiDispatcherTests
{
    [Fact]
    public async Task Native_ui_routes_all_propagate_dispatch_rejection()
    {
        var dispatcher = new AwaitableUiDispatcher(() => false, _ => false);
        var actions = new CompanionUiActions(
            token => dispatcher.InvokeAsync(() => { }, token),
            (_, token) => dispatcher.InvokeAsync(() => { }, token),
            token => dispatcher.InvokeAsync(() => { }, token),
            navigateSettingsDestination: (_, token) => dispatcher.InvokeAsync(() => { }, token));

        await AssertRejectedAsync(() => actions.OpenHome(TestContext.Current.CancellationToken));
        await AssertRejectedAsync(() => actions.OpenSettings(null!, TestContext.Current.CancellationToken));
        await AssertRejectedAsync(() => actions.NavigateSettingsDestination!("tasks", TestContext.Current.CancellationToken));
        await AssertRejectedAsync(() => actions.Exit(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enqueued_callback_is_awaited_and_its_failure_propagates()
    {
        Action? queued = null;
        var dispatcher = new AwaitableUiDispatcher(
            () => false,
            callback =>
            {
                queued = callback;
                return true;
            });
        var operation = dispatcher.InvokeAsync(
            () => throw new InvalidOperationException("UI route failed"),
            TestContext.Current.CancellationToken);

        Assert.False(operation.IsCompleted);
        Assert.NotNull(queued);
        queued();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal("UI route failed", exception.Message);
    }

    private static async Task AssertRejectedAsync(Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(operation);
        Assert.Contains("dispatcher rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
