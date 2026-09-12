using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class CompanionCompositionTests
{
    [Theory]
    [InlineData("--background", true)]
    [InlineData("--background --other", true)]
    [InlineData("--other", false)]
    [InlineData("", false)]
    public void Launch_arguments_parse_background_mode(string arguments, bool expected)
    {
        Assert.Equal(expected, CompanionLaunchOptions.Parse(arguments).Background);
    }

    [Fact]
    public void Background_launch_uses_persisted_startup_policy_for_overlay_visibility()
    {
        var background = CompanionLaunchOptions.Parse("--background");
        var startupEnabled = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, true, false, true, TimeSpan.FromMinutes(15));
        var startupDisabled = startupEnabled with { LaunchAtSignIn = false };

        Assert.False(background.ShouldShowOverlay(startupEnabled));
        Assert.True(background.ShouldShowOverlay(startupDisabled));
        Assert.True(new CompanionLaunchOptions(false).ShouldShowOverlay(startupEnabled));
    }

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
    public async Task Production_settings_dispatch_awaits_ui_callback_and_surfaces_failure()
    {
        var destinations = new List<string>();
        var actions = new CompanionUiActions(
            () => { },
            _ => { },
            () => { },
            navigateSettingsDestination: (destination, _) =>
            {
                destinations.Add(destination);
                return Task.CompletedTask;
            });

        await WindowsCompanionProductionComposition.DispatchSettingsDestinationAsync(
            actions,
            "tasks",
            TestContext.Current.CancellationToken);
        Assert.Equal(["tasks"], destinations);

        var unavailable = new CompanionUiActions(() => { }, _ => { }, () => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsCompanionProductionComposition.DispatchSettingsDestinationAsync(
                unavailable,
                "notes",
                TestContext.Current.CancellationToken));

        var rejected = new CompanionUiActions(
            () => { },
            _ => { },
            () => { },
            navigateSettingsDestination: (_, _) => Task.FromException(
                new InvalidOperationException("dispatcher rejected")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsCompanionProductionComposition.DispatchSettingsDestinationAsync(
                rejected,
                "notes",
                TestContext.Current.CancellationToken));
        Assert.Equal("dispatcher rejected", exception.Message);
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
