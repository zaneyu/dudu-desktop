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
[Collection(WindowsUiCollection.Name)]
public sealed class SettingsNavigationTests
{
    [Fact]
    public void Settings_journey_persists_features_and_executes_accessible_overlay_routes()
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

        var root = Path.Combine(Path.GetTempPath(), "dudu-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", root);
        Application? application = null;
        try
        {
            application = Application.Launch(executable);
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The Dudu settings window did not appear.");

            CompleteOnboardingWhenNeeded(window);
            VerifyNavigationContract(window);

            var marker = Guid.NewGuid().ToString("N")[..8];
            var reminderTitle = $"UI reminder {marker}";
            Navigate(window, "NavReminders", "RemindersPageTitle");
            Find(window, "RemindersTitle").AsTextBox().Enter(reminderTitle);
            Find(window, "RemindersSave").AsButton().Invoke();
            WaitForText(window, "RemindersStatusMessage", "reminder saved");
            SelectByName(window, "RemindersList", reminderTitle);
            Find(window, "RemindersComplete").AsButton().Invoke();
            WaitForText(window, "RemindersStatusMessage", "done le good job");

            var taskTitle = $"UI task {marker}";
            Navigate(window, "NavTasksFocus", "TasksPageTitle");
            Find(window, "TasksTitle").AsTextBox().Enter(taskTitle);
            Find(window, "TasksSave").AsButton().Invoke();
            WaitForText(window, "TasksStatusMessage", "task saved");
            SelectByName(window, "TasksActiveList", taskTitle);
            Find(window, "TasksComplete").AsButton().Invoke();
            WaitForText(window, "TasksStatusMessage", "task done");
            Assert.NotNull(Find(window, "TasksCompletedList").FindFirstDescendant(cf => cf.ByName(taskTitle)));

            Find(window, "FocusStart").AsButton().Invoke();
            WaitForText(window, "FocusCurrent", "Focus is running");
            Find(window, "FocusEnd").AsButton().Invoke();
            WaitForText(window, "TasksStatusMessage", "good job rest rest abit");
            WaitForText(window, "FocusCurrent", "ended early");
            Assert.NotNull(Find(window, "FocusHistoryList").FindFirstDescendant(cf => cf.ByName("EndedEarly")));

            Navigate(window, "NavHome", "HomePageTitle");
            Find(window, "HomeCheckInNote").AsTextBox().Enter($"check-in-{marker}");
            Find(window, "HomeSaveCheckIn").AsButton().Invoke();
            WaitForText(window, "HomeStatusMessage", "oki noted mwamwa");
            WaitForText(window, "HomeCheckInSummary", "1 optional check-in");
            Assert.NotNull(Find(window, "HomeCheckInHistory").FindFirstDescendant(
                cf => cf.ByName($"check-in-{marker}")));

            ExerciseAccessibleRoutes(window);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previousRoot);
            if (application is not null)
            {
                try { application.Close(); }
                catch { application.Kill(); }
            }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyNavigationContract(Window window)
    {
        foreach (var destination in Destinations)
        {
            Navigate(window, destination.NavigationId, destination.PageTitleId);
            foreach (var actionId in destination.RequiredControlIds)
            {
                Assert.NotNull(TryFind(window, actionId));
            }
        }
        Assert.False(Find(window, "AppearanceOutfit").Properties.IsEnabled.ValueOrDefault);
        Assert.False(Find(window, "AppearanceSeasonalMode").Properties.IsEnabled.ValueOrDefault);
    }

    private static void ExerciseAccessibleRoutes(Window window)
    {
        ExerciseRoute(window, "OverlayActionPet", "HomePageTitle", "HomeActionStatus", "pet is ready.");
        ExerciseRoute(window, "OverlayActionDrinkWater", "RemindersPageTitle");
        ExerciseRoute(window, "OverlayActionTasks", "TasksPageTitle");
        ExerciseRoute(window, "OverlayActionLoveNote", "LoveNotesPageTitle");
        ExerciseRoute(window, "OverlayActionComfortMe", "HomePageTitle", "HomeActionStatus", "comfort me is ready.");
        ExerciseRoute(window, "OverlayComfortActionBreatheWithMe", "HomePageTitle", "HomeActionStatus", "breathe with me is ready.");
        ExerciseRoute(window, "OverlayComfortActionTinyHug", "HomePageTitle", "HomeActionStatus", "tiny hug is ready.");
        ExerciseRoute(window, "OverlayComfortActionReadALoveNote", "LoveNotesPageTitle");
        ExerciseRoute(window, "OverlayComfortActionTakeAFiveMinuteBreak", "HomePageTitle", "HomeActionStatus", "take a five-minute break is ready.");
        WaitForText(window, "HomePauseDescription", "five minutes");
        ExerciseRoute(window, "OverlayComfortActionClose", "HomePageTitle", "HomeActionStatus", "close is ready.");

        Navigate(window, "NavHome", "HomePageTitle");
        Find(window, "OverlayActionStartFocus").AsButton().Invoke();
        WaitUntil(() => IsVisible(window, "TasksPageTitle"));
        WaitForText(window, "FocusCurrent", "Focus is running");
        Find(window, "FocusEnd").AsButton().Invoke();
        WaitForText(window, "TasksStatusMessage", "good job rest rest abit");
    }

    private static void ExerciseRoute(
        Window window,
        string actionId,
        string destinationTitleId,
        string? statusId = null,
        string? statusText = null)
    {
        Navigate(window, "NavHome", "HomePageTitle");
        Find(window, actionId).AsButton().Invoke();
        WaitUntil(() => IsVisible(window, destinationTitleId));
        if (statusId is not null && statusText is not null)
        {
            WaitForText(window, statusId, statusText);
        }
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
        new("NavLoveNotes", "LoveNotesPageTitle", ["LoveNotesLocalList", "LoveNotesRemoteList", "LoveNotesRevealSelected", "LoveNotesSave"]),
        new("NavAppearance", "AppearancePageTitle", ["AppearanceSave", "AppearanceOutfit", "AppearanceSeasonalMode"]),
        new("NavConnection", "ConnectionPageTitle", ["ConnectionCreateCode", "ConnectionSessionsList"]),
        new("NavPrivacy", "PrivacyPageTitle", ["PrivacyStoredFields", "PrivacyBackup", "PrivacyRestore", "PrivacyDeleteLocal", "PrivacyDeleteRemote", "PrivacyConfirm"]),
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
        Find(window, "OnboardingReducedMotion").AsCheckBox().IsChecked = true;
        Advance(window, "OnboardingQuietHoursEnabled");
        Advance(window, "OnboardingHydrationReminders");
        Find(window, "OnboardingLaunchAtSignIn").AsCheckBox().IsChecked = false;
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

    private static void Navigate(Window window, string navigationId, string pageTitleId)
    {
        Find(window, navigationId).Click();
        WaitUntil(() => IsVisible(window, pageTitleId));
    }

    private static void SelectByName(Window window, string listId, string name)
    {
        AutomationElement? item = null;
        WaitUntil(() =>
        {
            item = Find(window, listId).FindFirstDescendant(cf => cf.ByName(name));
            return item is not null;
        });
        item!.Click();
    }

    private static void WaitForText(Window window, string automationId, string expected) =>
        WaitUntil(() => (TryFind(window, automationId)?.Properties.Name.ValueOrDefault ?? string.Empty)
            .Contains(expected, StringComparison.OrdinalIgnoreCase));

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(15).TotalSeconds);
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
