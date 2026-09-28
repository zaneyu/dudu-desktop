using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Tray;

/// <summary>
/// The tray menu is a fixed list of four items. The native menu maps a click
/// back by index (item id = index + 1, resolved on WM_COMMAND), so the list
/// must never change length with pause state.
/// </summary>
public sealed class TrayMenuFixedItemsTests
{
    private const uint WmCommand = 0x0111;
    private const int WmRButtonUp = 0x0205;
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static readonly TrayCommand[] ExpectedCommands =
    [
        TrayCommand.ShowOrHide,
        TrayCommand.PauseOneHourOrResume,
        TrayCommand.OpenSettings,
        TrayCommand.Exit,
    ];

    public static TheoryData<PauseMode> AllPauseModes()
    {
        var data = new TheoryData<PauseMode>();
        foreach (var mode in Enum.GetValues<PauseMode>()) data.Add(mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPauseModes))]
    public void Menu_has_the_same_four_items_in_every_pause_state(PauseMode mode)
    {
        var native = new RecordingTrayNativeApi();
        using var service = new TrayIconService(
            native,
            _ => { },
            menuState: () => new TrayMenuState(PetVisible: true, mode));
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, WmRButtonUp));

        var items = native.LastItems!;
        Assert.Equal(4, items.Count);
        Assert.Equal(ExpectedCommands, items.Select(item => item.Command));
        Assert.Equal(ExpectedCommands, service.Commands);
        Assert.Equal(
            mode == PauseMode.None ? "pause 1 hour" : "resume dudu",
            items[1].Label);
        Assert.Equal("open dudu", items[2].Label);
        Assert.Equal("exit", items[3].Label);
    }

    [Theory]
    [MemberData(nameof(AllPauseModes))]
    public void Item_ids_one_to_four_resolve_to_the_labelled_command(PauseMode mode)
    {
        var seen = new List<TrayCommand>();
        using var service = new TrayIconService(
            new RecordingTrayNativeApi(),
            seen.Add,
            menuState: () => new TrayMenuState(PetVisible: false, mode));
        service.Attach(42);
        var labelled = service.BuildMenu();

        for (var id = 1; id <= 4; id++)
        {
            Assert.True(service.HandleWindowMessage(WmCommand, id, 0));
        }

        Assert.Equal(labelled.Select(item => item.Command), seen);
        Assert.Equal(ExpectedCommands, seen);
        Assert.False(service.HandleWindowMessage(WmCommand, 0, 0));
        Assert.False(service.HandleWindowMessage(WmCommand, 5, 0));
    }

    [Fact]
    public async Task Pause_item_pauses_for_one_hour_when_not_paused()
    {
        var pause = new PauseStateStore();
        await using var lifecycle = CreateLifecycle(pause);
        var router = CreateRouter(lifecycle, pause);

        await router.HandleAsync(TrayCommand.PauseOneHourOrResume, TestContext.Current.CancellationToken);

        Assert.Equal(PausePolicy.ForOneHour(Now), pause.Current);
        Assert.Equal(new PauseState(PauseMode.OneHour, Now.AddHours(1)), pause.Current);
    }

    [Theory]
    [InlineData(PauseMode.OneHour)]
    [InlineData(PauseMode.FiveMinutes)]
    [InlineData(PauseMode.UntilTomorrowAtSeven)]
    [InlineData(PauseMode.UntilFullscreenEnds)]
    [InlineData(PauseMode.Indefinite)]
    public async Task Pause_item_resumes_any_pause_including_legacy_persisted_modes(PauseMode mode)
    {
        var pause = new PauseStateStore();
        // Restored the way startup restores a persisted (possibly legacy) pause.
        pause.Restore(new PauseState(
            mode,
            mode is PauseMode.UntilFullscreenEnds or PauseMode.Indefinite ? null : Now.AddHours(2)));
        await using var lifecycle = CreateLifecycle(pause);
        var router = CreateRouter(lifecycle, pause);

        await router.HandleAsync(TrayCommand.PauseOneHourOrResume, TestContext.Current.CancellationToken);

        Assert.Equal(PauseState.None, pause.Current);
    }

    [Fact]
    public async Task Pause_item_label_and_router_agree_through_a_full_toggle()
    {
        var pause = new PauseStateStore();
        await using var lifecycle = CreateLifecycle(pause);
        var router = CreateRouter(lifecycle, pause);
        var native = new RecordingTrayNativeApi();
        using var service = new TrayIconService(
            native,
            _ => { },
            menuState: () => new TrayMenuState(PetVisible: true, pause.Current.Mode));
        service.Attach(42);

        Assert.Equal("pause 1 hour", service.BuildMenu()[1].Label);
        await router.HandleAsync(service.BuildMenu()[1].Command, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.OneHour, pause.Current.Mode);

        Assert.Equal("resume dudu", service.BuildMenu()[1].Label);
        await router.HandleAsync(service.BuildMenu()[1].Command, TestContext.Current.CancellationToken);
        Assert.Equal(PauseMode.None, pause.Current.Mode);
        Assert.Equal("pause 1 hour", service.BuildMenu()[1].Label);
    }

    private static CompanionCommandRouter CreateRouter(AppLifecycleCoordinator lifecycle, PauseStateStore pause) =>
        new(
            lifecycle,
            pause,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => Now);

    private static AppLifecycleCoordinator CreateLifecycle(PauseStateStore pause) =>
        new(
            new FakeHost(),
            new FakeOverlay(),
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                false, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause.Current,
            clock: () => Now);

    private sealed class RecordingTrayNativeApi : ITrayNativeApi
    {
        public IReadOnlyList<TrayMenuItem>? LastItems { get; private set; }

        public bool Add(nint ownerWindow, uint callbackMessage, string tooltip) => true;

        public bool Remove(nint ownerWindow) => true;

        public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip) => true;

        public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayMenuItem> items)
        {
            LastItems = items.ToArray();
            return null;
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
