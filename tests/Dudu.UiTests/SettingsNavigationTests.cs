using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only UI Automation coverage for all live settings destinations.
/// It asserts the normal-control equivalents of the no-activate overlay,
/// which keeps every companion action reachable without a mouse.
/// </summary>
public sealed class SettingsNavigationTests
{
    [Fact]
    public void Settings_navigation_visits_all_pages_and_exposes_overlay_equivalents()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FlaUI settings navigation requires Windows UI Automation.");
        }

        var executable = Environment.GetEnvironmentVariable("DUDU_UI_TEST_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Assert.Skip("Set DUDU_UI_TEST_EXE to a Windows publish output to run UI automation.");
        }

        using var application = Application.Launch(executable);
        using var automation = new UIA3Automation();
        var window = application.GetMainWindow(automation)
            ?? throw new InvalidOperationException("The Dudu settings window did not appear.");

        CompleteOnboardingWhenNeeded(window);

        var navigationClicks = 0;
        foreach (var destination in Destinations)
        {
            navigationClicks++;
            Find(window, destination.NavigationId).Click();
            WaitUntil(() => IsVisible(window, destination.PageTitleId));
            foreach (var actionId in destination.RequiredControlIds)
            {
                Assert.True(IsVisible(window, actionId), $"{actionId} should be reachable on {destination.NavigationId}.");
            }
        }

        Assert.Equal(7, navigationClicks);
        Assert.False(Find(window, "AppearanceOutfit").Properties.IsEnabled.ValueOrDefault);
        Assert.False(Find(window, "AppearanceSeasonalMode").Properties.IsEnabled.ValueOrDefault);
    }

    private static readonly Destination[] Destinations =
    [
        new(
            "NavHome",
            "HomePageTitle",
            [
                "OverlayActionPet", "OverlayActionDrinkWater", "OverlayActionStartFocus",
                "OverlayActionTasks", "OverlayActionLoveNote", "OverlayActionComfortMe",
                "OverlayComfortActionBreatheWithMe", "OverlayComfortActionTinyHug",
                "OverlayComfortActionReadALoveNote", "OverlayComfortActionTakeAFiveMinuteBreak",
                "OverlayComfortActionClose", "HomeCheckInHistory",
            ]),
        new("NavReminders", "RemindersPageTitle", ["RemindersMonday", "RemindersFriday", "RemindersSave"]),
        new("NavTasksFocus", "TasksPageTitle", ["TasksCompletedList", "FocusHistoryList", "FocusStart", "FocusEnd"]),
        new("NavLoveNotes", "LoveNotesPageTitle", ["LoveNotesLocalList", "LoveNotesRemoteList", "LoveNotesSave"]),
        new("NavAppearance", "AppearancePageTitle", ["AppearanceSave", "AppearanceOutfit", "AppearanceSeasonalMode"]),
        new("NavConnection", "ConnectionPageTitle", ["ConnectionCreateCode", "ConnectionSessionsList"]),
        new("NavPrivacy", "PrivacyPageTitle", ["PrivacyStoredFields", "PrivacyBackup"]),
    ];

    private static void CompleteOnboardingWhenNeeded(Window window)
    {
        if (TryFind(window, "OnboardingRecipientName") is null)
        {
            WaitUntil(() => TryFind(window, "NavHome") is not null);
            return;
        }

        Find(window, "OnboardingRecipientName").AsTextBox().Enter("Mia");
        Find(window, "OnboardingRecommendedDefaults").AsButton().Invoke();
        Advance(window, "OnboardingTheme");
        Advance(window, "OnboardingQuietHoursEnabled");
        Advance(window, "OnboardingHydrationReminders");
        Advance(window, "OnboardingPlacementStep");
        Advance(window, "OnboardingSkipPairing");
        Find(window, "OnboardingSkipPairing").AsButton().Invoke();
        Find(window, "OnboardingComplete").AsButton().Invoke();
        WaitUntil(() => TryFind(window, "NavHome") is not null);
    }

    private static void Advance(Window window, string visibleControlId)
    {
        Find(window, "OnboardingNext").AsButton().Invoke();
        WaitUntil(() => IsVisible(window, visibleControlId));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(5).TotalSeconds);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Dudu settings navigation.");
            }

            Thread.Sleep(50);
        }
    }

    private static AutomationElement Find(Window window, string automationId) =>
        TryFind(window, automationId)
        ?? throw new InvalidOperationException($"Automation id '{automationId}' was not found.");

    private static AutomationElement? TryFind(Window window, string automationId) =>
        window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private static bool IsVisible(Window window, string automationId)
    {
        var element = TryFind(window, automationId);
        return element is not null && !element.Properties.IsOffscreen.ValueOrDefault;
    }

    private sealed record Destination(
        string NavigationId,
        string PageTitleId,
        IReadOnlyList<string> RequiredControlIds);
}
