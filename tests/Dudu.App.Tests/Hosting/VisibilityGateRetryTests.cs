using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// A single transient preference/clock/fullscreen fault must be retried and
/// logged, not leave the overlay hidden until the next external event.
/// </summary>
public sealed class VisibilityGateRetryTests
{
    [Fact]
    public async Task Transient_fullscreen_fault_is_retried_and_still_shows()
    {
        var overlay = new FakeOverlay { IsVisible = false };
        var reporter = new FakeReporter();
        var reads = 0;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            isQuietHours: () => false,
            isFullscreen: () =>
            {
                reads++;
                if (reads == 1)
                {
                    throw new InvalidOperationException("transient fullscreen fault");
                }

                return false;
            },
            clock: () => DateTimeOffset.UtcNow,
            errorReporter: reporter);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(2, reads);
        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
        Assert.Contains("show-fullscreen", reporter.Operations);
        Assert.DoesNotContain("show-fullscreen-retry", reporter.Operations);
    }

    [Fact]
    public async Task Transient_quiet_hours_fault_is_retried_and_still_shows()
    {
        var overlay = new FakeOverlay { IsVisible = false };
        var reporter = new FakeReporter();
        var reads = 0;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            isQuietHours: () =>
            {
                reads++;
                if (reads == 1)
                {
                    throw new InvalidOperationException("transient quiet-hours fault");
                }

                return false;
            },
            isFullscreen: () => false,
            clock: () => DateTimeOffset.UtcNow,
            errorReporter: reporter);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(2, reads);
        Assert.Equal(1, overlay.ShowCount);
        Assert.Contains("show-gate", reporter.Operations);
    }

    [Fact]
    public async Task Persistent_fullscreen_fault_stays_fail_closed_but_logs_both_attempts()
    {
        var overlay = new FakeOverlay { IsVisible = false };
        var reporter = new FakeReporter();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            isQuietHours: () => false,
            isFullscreen: () => throw new InvalidOperationException("stuck fullscreen fault"),
            clock: () => DateTimeOffset.UtcNow,
            errorReporter: reporter);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(0, overlay.ShowCount);
        Assert.Contains("show-fullscreen", reporter.Operations);
        Assert.Contains("show-fullscreen-retry", reporter.Operations);
    }

    [Fact]
    public async Task Startup_gate_retries_a_transient_sample_then_shows()
    {
        var samples = 0;
        var shown = 0;

        await StartupVisibilityGate.ApplyAsync(
            _ =>
            {
                samples++;
                if (samples == 1)
                {
                    throw new InvalidOperationException("transient sample fault");
                }

                return Task.CompletedTask;
            },
            (_, _) =>
            {
                shown++;
                return Task.CompletedTask;
            },
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples);
        Assert.Equal(1, shown);
    }

    private static Preferences TestPreferences() => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        false,
        3,
        true,
        false,
        true,
        TimeSpan.FromMinutes(15));

    private sealed class FakeHost : IAppHostLifecycle
    {
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOverlay : IOverlayLifecycle
    {
        public int ShowCount { get; private set; }
        public bool IsVisible { get; set; } = true;
        public void Show() { ShowCount++; IsVisible = true; }
        public void Hide() => IsVisible = false;
        public void RestorePlacement() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReporter : IAppHostErrorReporter
    {
        public List<string> Operations { get; } = [];

        public void Report(string operation, Exception exception) => Operations.Add(operation);
    }
}
