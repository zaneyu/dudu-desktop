using System.Diagnostics;
using Dudu.App.Animation;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class StopwatchAnimationClockTests
{
    [Fact]
    public async Task Short_frame_wait_raises_the_timer_resolution_and_releases_it()
    {
        var resolution = new RecordingResolution();
        var clock = resolution.CreateClock();

        await clock.DelayUntilAsync(DeadlineIn(20), TestContext.Current.CancellationToken);

        Assert.Equal(1, resolution.Begins);
        Assert.Equal(1, resolution.Ends);
    }

    [Fact]
    public async Task Cancelled_short_wait_still_releases_the_timer_resolution()
    {
        var resolution = new RecordingResolution();
        var clock = resolution.CreateClock();
        using var cancellation = new CancellationTokenSource();

        var wait = clock.DelayUntilAsync(DeadlineIn(200), cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, resolution.Begins);
        Assert.Equal(1, resolution.Ends);
    }

    [Fact]
    public async Task Refused_resolution_request_is_not_released()
    {
        var resolution = new RecordingResolution { Grant = false };
        var clock = resolution.CreateClock();

        await clock.DelayUntilAsync(DeadlineIn(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, resolution.Begins);
        Assert.Equal(0, resolution.Ends);
    }

    [Fact]
    public async Task Long_waits_and_past_deadlines_leave_the_timer_resolution_alone()
    {
        var resolution = new RecordingResolution();
        var clock = resolution.CreateClock();
        using var cancellation = new CancellationTokenSource();

        await clock.DelayUntilAsync(Stopwatch.GetTimestamp() - 1, TestContext.Current.CancellationToken);
        var longWait = clock.DelayUntilAsync(
            DeadlineIn((int)StopwatchAnimationClock.HighResolutionWaitCeilingMilliseconds + 500),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longWait);

        Assert.Equal(0, resolution.Begins);
        Assert.Equal(0, resolution.Ends);
    }

    private static long DeadlineIn(int milliseconds) =>
        Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;

    private sealed class RecordingResolution
    {
        private int _begins;
        private int _ends;

        public bool Grant { get; init; } = true;

        public int Begins => Volatile.Read(ref _begins);

        public int Ends => Volatile.Read(ref _ends);

        public StopwatchAnimationClock CreateClock() => new(
            () =>
            {
                Interlocked.Increment(ref _begins);
                return Grant;
            },
            () => Interlocked.Increment(ref _ends));
    }
}
