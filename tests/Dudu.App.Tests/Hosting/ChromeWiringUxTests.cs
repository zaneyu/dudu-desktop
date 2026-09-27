using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// Production wiring for chrome fixes whose building blocks already existed but were
/// never connected: the tray menu's state provider, the
/// fullscreen reading that lets "pause until fullscreen ends" end, the audio pause
/// gate, and the toast click router. The composition root needs WinUI to run, so the
/// wiring is pinned as source text (like ProductionStartupContractTests) and the
/// extracted helper is exercised directly.
/// </summary>
public sealed class ChromeWiringUxTests
{
    [Fact]
    public void Runtime_gives_the_tray_its_menu_state_and_the_pet_no_context_menu()
    {
        var runtime = ReadHosting("WindowsCompanionBootstrap.cs");
        var create = Slice(runtime, "public static async Task<WindowsCompanionRuntime> CreateAsync(", "catch\n");

        Assert.DoesNotContain("showContextMenu", create);
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
        Assert.Contains("new PauseStateStore(\n                isFullscreen: () => presentationGateway?.IsFullscreen ?? false,", composition);

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

        Assert.DoesNotContain("`pet-context-menu`", agents);
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
}
