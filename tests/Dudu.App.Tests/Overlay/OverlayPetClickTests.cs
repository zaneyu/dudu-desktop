using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

/// <summary>
/// Clicking Dudu's body still pets it after the action bubble was removed:
/// the overlay's dispatch queue runs the router's pet action in click order
/// and reports (never rethrows) a failure.
/// </summary>
public sealed class OverlayPetClickTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_body_click_runs_the_pet_handler_once()
    {
        var calls = 0;
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.EnqueuePet(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Clicks_are_serialized_in_click_order()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.EnqueuePet(async _ =>
        {
            lock (calls) calls.Add("first");
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        queue.EnqueuePet(_ =>
        {
            lock (calls) calls.Add("second");
            return Task.CompletedTask;
        });
        lock (calls) Assert.Equal(["first"], calls);
        releaseFirst.TrySetResult();
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], calls);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_throwing_pet_handler_is_reported_not_rethrown_and_later_clicks_still_run()
    {
        var failure = new InvalidOperationException("reaction failed");
        var reported = new List<Exception>();
        var ran = false;
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.EnqueuePet(_ => Task.FromException(failure));
        queue.EnqueuePet(_ => throw new InvalidOperationException("sync failure"));
        queue.EnqueuePet(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(2, reported.Count);
        Assert.Same(failure, reported[0]);
        Assert.True(ran);
    }

    [Fact]
    public async Task Disposing_the_queue_cancels_a_running_click_without_reporting_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new List<Exception>();
        var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.EnqueuePet(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        queue.Dispose();
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task The_production_pet_handler_plays_the_petted_reaction()
    {
        var presented = new List<string>();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (_, dismissalId, _) =>
        {
            lock (presented) presented.Add(dismissalId);
            return Task.CompletedTask;
        });
        var router = new OverlayCommandRouter(context, (_, _) => Task.CompletedTask);
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        // Exactly what the composition wires via OverlayWindowHost.SetPetHandlerAsync.
        queue.EnqueuePet(token => router.ExecuteAsync(OverlayAction.Pet, token));
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(["petted"], presented);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_failed_pet_from_a_click_is_reported_through_the_queue()
    {
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (_, _, _) =>
            Task.FromException(new InvalidOperationException("reaction failed")));
        var router = new OverlayCommandRouter(context, (_, _) => Task.CompletedTask);
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.EnqueuePet(token => router.ExecuteAsync(OverlayAction.Pet, token));
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal("reaction failed", Assert.Single(reported).Message);
    }

    [Fact]
    public void The_idle_activity_busy_check_no_longer_references_the_action_surface()
    {
        var composition = OverlaySourceFiles.Read(
            "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs");

        Assert.Contains("Busy: pet.Current.State != PetState.Idle);", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("actionSurface", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOpen);", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_click_is_wired_to_the_router_pet_action()
    {
        var composition = OverlaySourceFiles.Read(
            "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs");
        var host = OverlaySourceFiles.Read("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");

        // Declared before the try so the overlay-start callback can see the
        // router that is assigned after it (a later `var` there is CS0841).
        Assert.Contains("OverlayCommandRouter? overlayRouter = null;", composition, StringComparison.Ordinal);
        Assert.Contains("overlayRouter = new OverlayCommandRouter(", composition, StringComparison.Ordinal);
        Assert.Contains(
            "token => overlayRouter?.ExecuteAsync(OverlayAction.Pet, token) ?? Task.CompletedTask",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("public Task SetPetHandlerAsync(", host, StringComparison.Ordinal);
        Assert.Contains("_actionDispatchQueue.EnqueuePet(petAsync);", host, StringComparison.Ordinal);
    }
}
