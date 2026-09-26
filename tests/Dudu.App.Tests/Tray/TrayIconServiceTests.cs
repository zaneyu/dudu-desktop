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

    [Fact]
    public void Popup_menu_does_not_hold_service_lock_during_native_call()
    {
        var native = new FakeTrayNativeApi();
        var commands = new List<TrayCommand>();
        using var service = new TrayIconService(native, commands.Add);
        native.OnTrack = () =>
        {
            var task = Task.Run(() => service.ExecuteCommand(TrayCommand.Exit));
            Assert.True(task.Wait(TimeSpan.FromSeconds(1)));
        };
        service.Attach(42);

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, 0x0205));
        Assert.Equal([TrayCommand.Exit], commands);
    }

    [Fact]
    public void Popup_menu_labels_use_the_state_dependent_override_and_fall_back_to_defaults()
    {
        var native = new FakeTrayNativeApi();
        var paused = true;
        using var service = new TrayIconService(
            native,
            _ => { },
            labelOverride: command =>
            {
                if (command == TrayCommand.Exit) throw new InvalidOperationException("label failed");
                return command == TrayCommand.PauseIndefinitelyOrResume && paused ? "resume dudu" : null;
            });
        service.Attach(42);
        var commands = service.Commands.ToList();

        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, 0x0205));
        var labels = native.LastLabels!;
        Assert.Equal(commands.Count, labels.Count);
        Assert.Equal("resume dudu", labels[commands.IndexOf(TrayCommand.PauseIndefinitelyOrResume)]);
        // A faulting override never breaks the menu: the default label is used.
        Assert.Equal("exit", labels[commands.IndexOf(TrayCommand.Exit)]);
        Assert.Equal("show or hide dudu", labels[0]);

        paused = false;
        Assert.True(service.HandleWindowMessage(TrayIconService.CallbackMessage, 0x0205));
        Assert.Equal(
            TrayIconService.DefaultLabel(TrayCommand.PauseIndefinitelyOrResume),
            native.LastLabels![commands.IndexOf(TrayCommand.PauseIndefinitelyOrResume)]);
    }

    [Fact]
    public void Native_popup_menu_takes_the_foreground_and_posts_wm_null_around_track_popup_menu()
    {
        // KB135788: without SetForegroundWindow(owner) before TrackPopupMenuEx
        // the menu never dismisses when she clicks elsewhere and gets no
        // keyboard focus; without a message posted to the owner right after,
        // the next right-click closes the menu instead of opening it.
        var source = ReadTraySource();
        var start = source.IndexOf("internal static unsafe class NativeTrayMenu", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected NativeTrayMenu to still exist.");
        var body = source[start..];

        var foreground = body.IndexOf("PInvoke.SetForegroundWindow(owner)", StringComparison.Ordinal);
        var track = body.IndexOf("PInvoke.TrackPopupMenuEx(", StringComparison.Ordinal);
        var post = body.IndexOf("PInvoke.PostMessage(owner, WmNull", StringComparison.Ordinal);

        Assert.True(foreground >= 0, "Expected SetForegroundWindow(owner) in NativeTrayMenu.Show.");
        Assert.True(track > foreground, "Expected SetForegroundWindow before TrackPopupMenuEx.");
        Assert.True(post > track, "Expected PostMessage(owner, WM_NULL) after TrackPopupMenuEx.");
        Assert.Contains("private const uint WmNull = 0x0000;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Tray_icon_loads_the_embedded_app_icon_before_the_generic_system_icon()
    {
        // The tray used LoadIcon(HINSTANCE.Null, IDI_APPLICATION) -- the
        // generic Windows application icon -- instead of Dudu's own icon.
        var source = ReadTraySource();
        var notifyStart = source.IndexOf("internal static unsafe class NativeShellNotifyIcon", StringComparison.Ordinal);
        var notifyEnd = source.IndexOf("internal static unsafe class TrayAppIcon", StringComparison.Ordinal);
        Assert.True(notifyStart >= 0 && notifyEnd > notifyStart);
        var notify = source[notifyStart..notifyEnd];
        Assert.Contains("hIcon = new HICON((void*)TrayAppIcon.Handle)", notify, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadIcon(HINSTANCE.Null", notify, StringComparison.Ordinal);

        var icon = source[notifyEnd..];
        var embedded = icon.IndexOf("LoadImageW(module, ApplicationIconResourceId", StringComparison.Ordinal);
        var extracted = icon.IndexOf("ExtractIconExW(processPath", StringComparison.Ordinal);
        var fallback = icon.IndexOf("PInvoke.LoadIcon(HINSTANCE.Null", StringComparison.Ordinal);
        Assert.True(embedded >= 0, "Expected the embedded app icon to be loaded from the executable module.");
        Assert.True(extracted > embedded, "Expected the exe's first icon group as the second choice.");
        Assert.True(fallback > extracted, "Expected IDI_APPLICATION only as the last resort.");
    }

    private static string ReadTraySource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "Dudu.App", "Tray", "TrayIconService.cs"));
    }

    private sealed class FakeTrayNativeApi : ITrayNativeApi
    {
        public int AddCount { get; private set; }
        public int RemoveCount { get; private set; }
        public int RecreateCount { get; private set; }
        public int MenuCount { get; private set; }
        public TrayCommand? Selected { get; init; }
        public Action? OnTrack { get; set; }

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
            OnTrack?.Invoke();
            return Selected;
        }

        public IReadOnlyList<string>? LastLabels { get; private set; }

        public TrayCommand? TrackPopupMenu(
            nint ownerWindow,
            IReadOnlyList<TrayCommand> commands,
            IReadOnlyList<string> labels)
        {
            LastLabels = labels;
            return TrackPopupMenu(ownerWindow, commands);
        }
    }
}
