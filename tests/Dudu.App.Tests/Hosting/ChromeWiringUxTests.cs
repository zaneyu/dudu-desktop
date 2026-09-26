using Dudu.App.Hosting;
using Dudu.App.Tray;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// Production wiring for chrome fixes whose building blocks already existed but were
/// never connected: the tray menu's state provider, the pet's right-click menu, the
/// fullscreen reading that lets "pause until fullscreen ends" end, the audio pause
/// gate, and the toast click router. The composition root needs WinUI to run, so the
/// wiring is pinned as source text (like ProductionStartupContractTests) and the
/// extracted helper is exercised directly.
/// </summary>
public sealed class ChromeWiringUxTests
{
    [Fact]
    public void Right_click_on_the_pet_shows_the_tray_menu()
    {
        var native = new RecordingTrayNativeApi();
        using var tray = new TrayIconService(native, _ => { });
        tray.Attach(42);

        WindowsCompanionRuntime.ShowTrayMenuFromPet(tray, errorReporter: null);

        Assert.Equal(1, native.MenuShown);
        Assert.Equal(42, native.LastOwner);
    }

    [Fact]
    public void Right_click_before_the_tray_exists_or_after_it_is_gone_is_a_quiet_no_op()
    {
        var reporter = new RecordingErrorReporter();
        WindowsCompanionRuntime.ShowTrayMenuFromPet(null, reporter);

        var native = new RecordingTrayNativeApi();
        var tray = new TrayIconService(native, _ => { });
        tray.Attach(42);
        tray.Dispose();
        WindowsCompanionRuntime.ShowTrayMenuFromPet(tray, reporter);

        Assert.Equal(0, native.MenuShown);
        Assert.Empty(reporter.Operations);
    }

    [Fact]
    public void A_failing_pet_menu_is_reported_and_never_escapes_the_window_procedure()
    {
        var reporter = new RecordingErrorReporter();
        using var tray = new TrayIconService(new RecordingTrayNativeApi { Throw = true }, _ => { });
        tray.Attach(42);

        WindowsCompanionRuntime.ShowTrayMenuFromPet(tray, reporter);

        Assert.Equal([WindowsCompanionRuntime.PetContextMenuOperation], reporter.Operations);
    }

    [Fact]
    public void Runtime_gives_the_tray_its_menu_state_and_the_pet_its_context_menu()
    {
        var runtime = ReadHosting("WindowsCompanionBootstrap.cs");
        var create = Slice(runtime, "public static async Task<WindowsCompanionRuntime> CreateAsync(", "catch\n");

        Assert.Contains("showContextMenu: () => ShowTrayMenuFromPet(tray, errorReporter)", create);
        Assert.Contains("menuState: () => new TrayMenuState(", create);
        Assert.Contains("menuOverlay.IsVisible,", create);
        Assert.Contains("pauseState?.Invoke().Mode ?? PauseMode.None", create);
        Assert.DoesNotContain("new TrayIconService(commandHandler: handler, errorReporter: errorReporter);", create);
    }

    [Fact]
    public void Production_pause_can_end_with_fullscreen_and_sounds_follow_the_real_pause_gate()
    {
        var composition = ReadHosting("WindowsCompanionProductionComposition.cs");

        Assert.DoesNotContain("new PauseStateStore()", composition);
        Assert.Contains("new PauseStateStore(\n                isFullscreen: () => presentationGateway?.IsFullscreen ?? false)", composition);

        // Any non-None mode used to count as paused, so "pause until fullscreen ends"
        // muted sounds before fullscreen had even started.
        Assert.DoesNotContain("isPaused: () => pause.GetEffective(DateTimeOffset.UtcNow).Mode != PauseMode.None", composition);
        var isPaused = Slice(composition, "isPaused: () =>", "},");
        Assert.Contains("PausePolicy.IsSuppressed(", isPaused);
        Assert.Contains("pause.GetEffective(now)", isPaused);
    }

    [Fact]
    public void Toast_clicks_go_through_the_router_with_real_reminder_actions()
    {
        var composition = ReadHosting("WindowsCompanionProductionComposition.cs");
        var invoked = Slice(composition, "private static void HandleNotificationInvoked(", "\n    }\n");

        Assert.Contains("router.HandleAsync(arguments", invoked);
        Assert.Contains("HandleNotificationInvoked(notificationRouter, invokedArgs.Arguments)", composition);
        Assert.Contains("() => reminderToastActions,", composition);
        Assert.Contains("reminderToastActions = new ReminderToastActions(", composition);
        Assert.Contains("featureContext.Reminders,", composition);
        Assert.Contains("featureContext.DismissReminderNotificationAsync,", composition);
        Assert.Contains("featureContext.DiscardHeldReminderAsync,", composition);
    }

    [Fact]
    public void New_diagnostics_operations_are_documented()
    {
        var agents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "AGENTS.md"));

        Assert.Contains("`pet-context-menu`", agents);
        Assert.Contains("`reminder-toast-action`", agents);
        Assert.Contains("`notification-invoked`", agents);
        Assert.Contains("`tray-menu-state`", agents);
    }

    private static string ReadHosting(string file) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Dudu.App", "Hosting", file))
            .Replace("\r\n", "\n");

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' not found.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{endMarker}' not found after '{startMarker}'.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingTrayNativeApi : ITrayNativeApi
    {
        public bool Throw { get; init; }
        public int MenuShown { get; private set; }
        public nint LastOwner { get; private set; }

        public bool Add(nint ownerWindow, uint callbackMessage, string tooltip) => true;
        public bool Remove(nint ownerWindow) => true;
        public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip) => true;

        public TrayCommand? TrackPopupMenu(nint ownerWindow, IReadOnlyList<TrayMenuItem> items)
        {
            if (Throw) throw new InvalidOperationException("menu failed");
            MenuShown++;
            LastOwner = ownerWindow;
            return null;
        }
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        private readonly List<string> _operations = new();
        public IReadOnlyList<string> Operations => _operations;
        public void Report(string operation, Exception exception) => _operations.Add(operation);
    }
}
