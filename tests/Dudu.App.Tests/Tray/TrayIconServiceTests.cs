using Dudu.App.Tray;
using Xunit;

namespace Dudu.App.Tests.Tray;

public sealed class TrayIconServiceTests
{
    [Fact]
    public void Tray_commands_are_complete_and_recreation_is_idempotent()
    {
        var native = new FakeTrayNativeApi();
        var commands = new List<TrayCommand>();
        using var service = new TrayIconService(native, commands.Add);

        service.Attach(42);
        service.Recreate();
        service.ExecuteCommand(TrayCommand.PauseUntilTomorrowAtSeven);
        service.HandleWindowMessage(TrayIconService.TaskbarCreatedFallbackMessage, 0);
        service.Dispose();
        service.Dispose();

        Assert.Equal(1, native.AddCount);
        Assert.Equal(2, native.RecreateCount);
        Assert.Equal(1, native.RemoveCount);
        Assert.Equal([TrayCommand.PauseUntilTomorrowAtSeven], commands);
        Assert.Equal(7, service.Commands.Count);
    }

    [Fact]
    public void Native_menu_selection_routes_the_selected_command()
    {
        var native = new FakeTrayNativeApi { Selected = TrayCommand.OpenSettings };
        var commands = new List<TrayCommand>();
        using var service = new TrayIconService(native, commands.Add);
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, 0x0205));
        Assert.Equal([TrayCommand.OpenSettings], commands);
        Assert.Equal(1, native.MenuCount);
    }

    private sealed class FakeTrayNativeApi : ITrayNativeApi
    {
        public int AddCount { get; private set; }
        public int RemoveCount { get; private set; }
        public int RecreateCount { get; private set; }
        public int MenuCount { get; private set; }
        public TrayCommand? Selected { get; init; }

        public bool Add(nint ownerWindow, uint callbackMessage, string tooltip)
        {
            AddCount++;
            return true;
        }

        public bool Remove(nint ownerWindow)
        {
            RemoveCount++;
            return true;
        }

        public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip)
        {
            RecreateCount++;
            return true;
        }

        public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayCommand> commands)
        {
            MenuCount++;
            return Selected;
        }
    }
}
