using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class AppLifecycleCoordinatorTests
{
    [Fact]
    public async Task Unlock_welcomes_back_only_when_all_gates_are_clear()
    {
        var host = new FakeHost();
        var overlay = new FakeOverlay();
        var pet = PetStateMachine.CreateIdle();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false,
            3,
            true,
            false,
            true,
            TimeSpan.FromMinutes(15));
        var quiet = false;
        var fullscreen = false;
        var pause = PauseState.None;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            host,
            overlay,
            pet,
            preferences,
            () => pause,
            () => quiet,
            () => fullscreen,
            () => new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero));

        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Equal(PetState.WelcomeBack, pet.Current.State);

        pet.Handle(new PetEvent.Dismissed("welcome-back"));
        quiet = true;
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Fullscreen_end_restores_visibility_without_welcome_back()
    {
        var overlay = new FakeOverlay();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isFullscreen: () => false);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(1, overlay.ShowCount);
    }

    [Fact]
    public async Task Fullscreen_end_restores_placement_but_respects_pause_gate()
    {
        var overlay = new FakeOverlay();
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => PausePolicy.ForOneHour(now),
            clock: () => now);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(0, overlay.ShowCount);
    }

    private sealed class FakeHost : IAppHostLifecycle
    {
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOverlay : IOverlayLifecycle
    {
        public int HideCount { get; private set; }
        public int ShowCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool IsVisible { get; private set; } = true;
        public void Show() { ShowCount++; IsVisible = true; }
        public void Hide() { HideCount++; IsVisible = false; }
        public void RestorePlacement() => RestoreCount++;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
