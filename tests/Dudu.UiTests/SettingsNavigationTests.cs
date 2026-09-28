using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only UI Automation coverage for the three settings destinations
/// (home, love notes, settings). It asserts the normal-control equivalents of
/// the no-activate overlay, which keeps every cute action reachable without a
/// mouse.
/// </summary>
[Collection(WindowsUiCollection.Name)]
public sealed class SettingsNavigationTests
{
    [Fact]
    public void Settings_journey_persists_settings_and_executes_accessible_home_actions()
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

            // Settings: a saved look survives navigating away and back. Onboarding
            // turned reduced motion on, so turning it off is a real change.
            Navigate(window, "NavSettings", "SettingsPageTitle");
            Assert.True(Find(window, "AppearanceReducedMotion").AsCheckBox().IsChecked);
            Find(window, "AppearanceReducedMotion").AsCheckBox().IsChecked = false;
            Find(window, "AppearanceSave").AsButton().Invoke();
            WaitForText(window, "AppearanceStatusMessage", "appearance saved");
            Navigate(window, "NavHome", "HomePageTitle");
            Navigate(window, "NavSettings", "SettingsPageTitle");
            Assert.False(Find(window, "AppearanceReducedMotion").AsCheckBox().IsChecked);

            // Delete my data asks first; cancel leaves everything in place.
            Find(window, "SettingsDeleteMyData").AsButton().Invoke();
            WaitUntil(() => IsVisible(window, "SettingsDeleteConfirm"));
            Assert.NotNull(TryFind(window, "SettingsDeleteCancel"));
            Find(window, "SettingsDeleteCancel").AsButton().Invoke();
            WaitUntil(() => !IsVisible(window, "SettingsDeleteConfirm"));

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

        // Only home, love notes and settings remain in the shell.
        foreach (var removedNavigationId in RemovedNavigationIds)
        {
            Assert.Null(TryFind(window, removedNavigationId));
        }

        // Still on Settings: outfits, seasonal dates and user backup/restore are gone.
        foreach (var removedControlId in RemovedSettingsControlIds)
        {
            Assert.Null(TryFind(window, removedControlId));
        }
    }

    private static void ExerciseAccessibleRoutes(Window window)
    {
        // Pet runs through the Home view model; it must not surface an error.
        Navigate(window, "NavHome", "HomePageTitle");
        Find(window, "OverlayActionPet").AsButton().Invoke();
        Assert.Null(TryFind(window, "HomeErrorMessage"));

        ExerciseRoute(window, "OverlayActionDrinkWater", "HomeActionStatus", "drink water ready le");
        // Eat together toggles: start the meal, then end it again.
        ExerciseRoute(window, "OverlayActionEatTogether", "HomeActionStatus", "eat together ready le");
        ExerciseRoute(window, "OverlayActionEatTogether", "HomeActionStatus", "eat together ready le");
        ExerciseRoute(window, "OverlayComfortActionTinyHug", "HomeActionStatus", "tiny hug ready le");

        // Breathe with me shows its phase on Home; Stop ends it without an error.
        // Reduced motion was turned off above, so this is the running exercise,
        // not the reduced-motion static instruction.
        Navigate(window, "NavHome", "HomePageTitle");
        Find(window, "OverlayComfortActionBreatheWithMe").AsButton().Invoke();
        WaitUntil(() => IsVisible(window, "HomeBreathingPanel"));
        WaitUntil(() => !string.IsNullOrWhiteSpace(
            TryFind(window, "HomeBreathingPhase")?.Properties.Name.ValueOrDefault));
        Find(window, "HomeBreathingStop").AsButton().Invoke();
        WaitUntil(() => !IsVisible(window, "HomeBreathingPanel"));
        WaitForText(window, "HomeActionStatus", "breathing stopped");
        Assert.Null(TryFind(window, "HomeErrorMessage"));
    }

    private static void ExerciseRoute(
        Window window,
        string actionId,
        string statusId,
        string statusText)
    {
        Navigate(window, "NavHome", "HomePageTitle");
        Find(window, actionId).AsButton().Invoke();
        WaitUntil(() => IsVisible(window, "HomePageTitle"));
        WaitForText(window, statusId, statusText);
    }

    private static readonly Destination[] Destinations =
    [
        new(
            "NavHome",
            "HomePageTitle",
            [
                "HomeGreeting", "HomePartnerClock", "HomeDuduImage",
                "OverlayActionPet", "OverlayActionDrinkWater", "OverlayActionEatTogether",
                "OverlayComfortActionTinyHug", "OverlayComfortActionBreatheWithMe",
            ]),
        new("NavLoveNotes", "LoveNotesPageTitle", ["LoveNotesPendingCount", "LoveNotesRemoteList", "LoveNotesRevealSelected", "LoveNotesOpenedList", "LoveNotesDelete"]),
        new(
            "NavSettings",
            "SettingsPageTitle",
            [
                "AppearanceTheme", "AppearanceReducedMotion", "AppearanceSoundsEnabled", "AppearanceSoundVolume",
                "AppearancePetScale", "AppearanceMonitor", "AppearanceAlwaysOnTop", "AppearanceHideFullscreen",
                "AppearanceSave", "AppearanceSavePlacement", "SettingsLaunchAtSignIn",
                "SettingsConnectionHeading", "ConnectionCreateCode", "ConnectionRefresh", "ConnectionSessionsList",
                "ConnectionRevokeSessions", "ConnectionDeleteRemote", "ConnectionForgetPairing",
                "SettingsStoredData", "SettingsDeleteMyData",
            ]),
    ];

    private static readonly string[] RemovedNavigationIds =
        ["NavReminders", "NavTasksFocus", "NavAppearance", "NavConnection", "NavPrivacy"];

    private static readonly string[] RemovedSettingsControlIds =
        ["AppearanceOutfit", "AppearanceSeasonalMode", "PrivacyBackup", "PrivacyRestore"];

    private static void CompleteOnboardingWhenNeeded(Window window)
    {
        if (TryFind(window, "OnboardingRecipientName") is null)
        {
            WaitUntil(() => TryFind(window, "NavHome") is not null);
            return;
        }

        Find(window, "OnboardingRecipientName").AsTextBox().Enter("Mia");
        Advance(window, "OnboardingTheme");
        Find(window, "OnboardingReducedMotion").AsCheckBox().IsChecked = true;
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
