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
    public void Background_sign_in_launch_still_shows_the_overlay()
    {
        var background = CompanionLaunchOptions.Parse("--background");
        var startupEnabled = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, true, false, true, TimeSpan.FromMinutes(15));
        var startupDisabled = startupEnabled with { LaunchAtSignIn = false };

        Assert.True(background.ShouldShowOverlay(startupEnabled));
        Assert.True(background.ShouldShowOverlay(startupDisabled));
        Assert.True(new CompanionLaunchOptions(false).ShouldShowOverlay(startupEnabled));
    }

    [Fact]
    public void Production_ui_actions_require_every_external_callback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(null!, (_, _) => Task.CompletedTask, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(_ => Task.CompletedTask, null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() =>
            new CompanionUiActions(_ => Task.CompletedTask, (_, _) => Task.CompletedTask, null!));
    }

    [Fact]
    public async Task Production_settings_dispatch_awaits_ui_callback_and_surfaces_failure()
    {
        var destinations = new List<string>();
        var actions = new CompanionUiActions(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
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

        var unavailable = new CompanionUiActions(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsCompanionProductionComposition.DispatchSettingsDestinationAsync(
                unavailable,
                "notes",
                TestContext.Current.CancellationToken));

        var rejected = new CompanionUiActions(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
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
            _ => { settings++; return Task.CompletedTask; },
            _ => { exit++; return Task.CompletedTask; },
            () => now);

        await router.HandleAsync(TrayCommand.PauseOneHour, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.OneHour, pause.Current.Mode);
        await router.HandleAsync(TrayCommand.PauseUntilTomorrowAtSeven, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.UntilTomorrowAtSeven, pause.Current.Mode);
        await router.HandleAsync(TrayCommand.PauseUntilFullscreenEnds, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.Current.Mode);
        // "pause indefinitely or resume" resumes from ANY active pause; it
        // used to turn this one into an indefinite pause instead.
        await router.HandleAsync(TrayCommand.PauseIndefinitelyOrResume, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.None, pause.Current.Mode);
        await router.HandleAsync(TrayCommand.PauseIndefinitelyOrResume, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.Indefinite, pause.Current.Mode);
        await router.HandleAsync(TrayCommand.PauseIndefinitelyOrResume, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.None, pause.Current.Mode);
        await router.HandleAsync(TrayCommand.OpenSettings, TestContext.Current.CancellationToken);
        await router.HandleAsync(TrayCommand.Exit, TestContext.Current.CancellationToken);
        Assert.Equal(1, settings);
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData(TrayCommand.PauseOneHour)]
    [InlineData(TrayCommand.PauseUntilTomorrowAtSeven)]
    [InlineData(TrayCommand.PauseUntilFullscreenEnds)]
    public async Task Pause_indefinitely_or_resume_resumes_from_a_timed_or_fullscreen_pause(TrayCommand pauseCommand)
    {
        var overlay = new FakeOverlay();
        var pause = new PauseStateStore();
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            Preferences.Default,
            pauseState: () => pause.GetEffective(now),
            clock: () => now);
        var router = new CompanionCommandRouter(
            lifecycle,
            pause,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => now);

        await router.HandleAsync(pauseCommand, TestContext.Current.CancellationToken);
        Assert.NotEqual(PauseMode.None, pause.Current.Mode);
        Assert.Equal(
            "resume dudu",
            CompanionCommandRouter.TrayLabelFor(TrayCommand.PauseIndefinitelyOrResume, pause, now));

        await router.HandleAsync(TrayCommand.PauseIndefinitelyOrResume, TestContext.Current.CancellationToken);

        Assert.Equal(PauseMode.None, pause.Current.Mode);
        Assert.Equal(
            "pause indefinitely",
            CompanionCommandRouter.TrayLabelFor(TrayCommand.PauseIndefinitelyOrResume, pause, now));
        Assert.Null(CompanionCommandRouter.TrayLabelFor(TrayCommand.Exit, pause, now));
    }

    [Theory]
    [InlineData(TrayCommand.OpenSettings)]
    [InlineData(TrayCommand.Exit)]
    public async Task Production_command_router_propagates_ui_route_failure(TrayCommand command)
    {
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            new FakeOverlay(),
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)));
        var router = new CompanionCommandRouter(
            lifecycle,
            new PauseStateStore(),
            _ => Task.FromException(new InvalidOperationException("settings dispatch failed")),
            _ => Task.FromException(new InvalidOperationException("exit dispatch failed")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Contains("dispatch failed", exception.Message, StringComparison.Ordinal);
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
