using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Tray;

/// <summary>Regression coverage for tray menu wording/state, tray clicks, and
/// the pause/resume toggle.</summary>
public sealed class TrayMenuUxTests
{
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Menu_labels_say_what_each_toggle_will_do_right_now()
    {
        var state = new TrayMenuState(PetVisible: true, PauseMode.None);
        var native = new RecordingTrayNativeApi();
        using var service = new TrayIconService(native, _ => { }, menuState: () => state);
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, WmRButtonUp));
        Assert.Equal("hide dudu", Label(native, TrayCommand.ShowOrHide));
        Assert.Equal("pause until i resume", Label(native, TrayCommand.PauseIndefinitelyOrResume));
        Assert.DoesNotContain(native.LastItems!, item => item.Checked);

        state = new TrayMenuState(PetVisible: false, PauseMode.OneHour);
        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, WmRButtonUp));
        Assert.Equal("show dudu", Label(native, TrayCommand.ShowOrHide));
        Assert.Equal("resume dudu", Label(native, TrayCommand.PauseIndefinitelyOrResume));
        var checkedItem = Assert.Single(native.LastItems!, item => item.Checked);
        Assert.Equal(TrayCommand.PauseOneHour, checkedItem.Command);
    }

    [Fact]
    public void Menu_keeps_neutral_labels_when_state_is_unavailable()
    {
        var native = new RecordingTrayNativeApi();
        using var service = new TrayIconService(
            native,
            _ => { },
            menuState: () => throw new InvalidOperationException("state gone"));
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, WmRButtonUp));

        Assert.Equal("show or hide dudu", Label(native, TrayCommand.ShowOrHide));
        Assert.Equal("pause indefinitely or resume", Label(native, TrayCommand.PauseIndefinitelyOrResume));
        Assert.Equal(service.Commands, native.LastItems!.Select(item => item.Command));
    }

    [Fact]
    public void A_left_click_on_the_tray_icon_opens_dudu()
    {
        var native = new RecordingTrayNativeApi();
        var commands = new List<TrayCommand>();
        using var service = new TrayIconService(native, commands.Add);
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, WmLButtonUp));

        Assert.Equal([TrayCommand.OpenSettings], commands);
        Assert.Null(native.LastItems);
    }

    [Fact]
    public void The_tray_menu_can_be_shown_from_another_surface_and_routes_its_choice()
    {
        var native = new RecordingTrayNativeApi { Selected = TrayCommand.PauseOneHour };
        var commands = new List<TrayCommand>();
        using var service = new TrayIconService(native, commands.Add);

        Assert.False(service.ShowMenu());
        service.Attach(42);

        Assert.True(service.ShowMenu());
        Assert.NotNull(native.LastItems);
        Assert.Equal([TrayCommand.PauseOneHour], commands);
    }

    [Theory]
    [InlineData(TrayCommand.PauseOneHour)]
    [InlineData(TrayCommand.PauseUntilTomorrowAtSeven)]
    [InlineData(TrayCommand.PauseUntilFullscreenEnds)]
    public async Task Pause_or_resume_resumes_any_active_pause_in_one_click(TrayCommand pauseCommand)
    {
        var pause = new PauseStateStore();
        await using var lifecycle = CreateLifecycle(pause);
        var router = new CompanionCommandRouter(
            lifecycle,
            pause,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => Now);

        await router.HandleAsync(pauseCommand, TestContext.Current.CancellationToken);
        Assert.NotEqual(PauseMode.None, pause.Current.Mode);

        await router.HandleAsync(TrayCommand.PauseIndefinitelyOrResume, TestContext.Current.CancellationToken);

        Assert.Equal(PauseMode.None, pause.Current.Mode);
    }

    [Fact]
    public void Pause_until_fullscreen_ends_ends_when_the_fullscreen_session_ends()
    {
        var fullscreen = false;
        var pause = new PauseStateStore(() => fullscreen);
        pause.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));

        // Not fullscreen yet: the pause waits for the fullscreen session.
        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.GetEffective(Now).Mode);
        fullscreen = true;
        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.GetEffective(Now).Mode);
        fullscreen = false;
        Assert.Equal(PauseMode.None, pause.GetEffective(Now).Mode);
        Assert.Equal(PauseMode.None, pause.Current.Mode);
    }

    [Fact]
    public void A_new_pause_does_not_inherit_an_earlier_fullscreen_sighting()
    {
        var fullscreen = true;
        var pause = new PauseStateStore(() => fullscreen);
        pause.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));
        _ = pause.GetEffective(Now);

        pause.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));
        fullscreen = false;

        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.GetEffective(Now).Mode);
    }

    [Fact]
    public void An_unreadable_fullscreen_state_keeps_the_pause()
    {
        var pause = new PauseStateStore(() => throw new InvalidOperationException("probe failed"));
        pause.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));

        Assert.Equal(PauseMode.UntilFullscreenEnds, pause.GetEffective(Now).Mode);
    }

    private static string Label(RecordingTrayNativeApi native, TrayCommand command) =>
        native.LastItems!.Single(item => item.Command == command).Label;

    private static AppLifecycleCoordinator CreateLifecycle(PauseStateStore pause) =>
        new(
            new FakeHost(),
            new FakeOverlay(),
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause.Current,
            clock: () => Now);

    private sealed class RecordingTrayNativeApi : ITrayNativeApi
    {
        public IReadOnlyList<TrayMenuItem>? LastItems { get; private set; }

        public TrayCommand? Selected { get; init; }

        public bool Add(nint ownerWindow, uint callbackMessage, string tooltip) => true;

        public bool Remove(nint ownerWindow) => true;

        public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip) => true;

        public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayMenuItem> items)
        {
            LastItems = items.ToArray();
            return Selected;
        }
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
