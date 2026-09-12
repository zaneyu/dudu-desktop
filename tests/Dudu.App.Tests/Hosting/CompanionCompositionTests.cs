using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class CompanionCompositionTests
{
    [Fact]
    public void Production_ui_actions_require_every_external_callback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(null!, _ => { }, () => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(() => { }, null!, () => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(() => { }, _ => { }, null!));
    }

    [Fact]
    public async Task Production_command_router_maps_pause_settings_and_exit()
    {
        var overlay = new FakeOverlay();
        var pause = new PauseStateStore();
        var settings = 0;
        var exit = 0;
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause.Current,
            clock: () => now);
        var router = new CompanionCommandRouter(
            lifecycle,
            pause,
            () => settings++,
            () => exit++,
            () => now);

        router.Handle(TrayCommand.PauseOneHour);
        Assert.Equal(PauseMode.OneHour, pause.Current.Mode);
        router.Handle(TrayCommand.PauseUntilTomorrowAtSeven);
        Assert.Equal(PauseMode.UntilTomorrowAtSeven, pause.Current.Mode);
        router.Handle(TrayCommand.PauseUntilFullscreenEnds);
        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.Current.Mode);
        router.Handle(TrayCommand.PauseIndefinitelyOrResume);
        Assert.Equal(PauseMode.Indefinite, pause.Current.Mode);
        router.Handle(TrayCommand.PauseIndefinitelyOrResume);
        Assert.Equal(PauseMode.None, pause.Current.Mode);
        router.Handle(TrayCommand.OpenSettings);
        router.Handle(TrayCommand.Exit);
        Assert.Equal(1, settings);
        Assert.Equal(1, exit);
    }

    private sealed class FakeHost : IAppHostLifecycle
    {
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOverlay : IOverlayLifecycle
    {
        public bool IsVisible { get; private set; } = true;
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void RestorePlacement() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
