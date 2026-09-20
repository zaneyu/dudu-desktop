using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only end-to-end coverage of the whole companion journey: onboarding, a reminder and a
/// task created and completed, a one-minute focus session run to completion, a check-in, revealing
/// a fixture remote note, an appearance change, a backup, a restart, and persistence across that
/// restart. The executable is supplied by the Windows publish job so this project remains
/// buildable on non-Windows hosts without weakening the WinUI product target.
/// </summary>
[Collection(WindowsUiCollection.Name)]
public sealed class FullJourneyTests
{
    // Must match the plaintext RemoteMessagePayload.Text that
    // src/Dudu.App/Hosting/FixtureRemoteNoteInstaller.cs encrypts for DUDU_FIXTURE_NOTE=1.
    private const string FixtureNoteText = "oki fixture note here for testing";

    [Fact]
    public void Full_companion_journey_persists_every_change_across_a_restart()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FlaUI full-journey coverage requires Windows UI Automation.");
        }

        var executable = Environment.GetEnvironmentVariable("DUDU_UI_TEST_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Assert.Skip("Set DUDU_UI_TEST_EXE to a Windows publish output to run UI automation.");
        }

        var root = Path.Combine(Path.GetTempPath(), "dudu-full-journey-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        var previousFixtureNote = Environment.GetEnvironmentVariable("DUDU_FIXTURE_NOTE");
        Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", root);
        // Every launch in this test (including the restart below) asks the app to install its
        // one fixture remote note. The insert is idempotent (fixed message id, ON CONFLICT
        // DO NOTHING), so asking again on relaunch is a safe no-op rather than a duplicate.
        Environment.SetEnvironmentVariable("DUDU_FIXTURE_NOTE", "1");

        Application? application = null;
        try
        {
            application = Application.Launch(executable);
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The Dudu settings window did not appear.");

            CompleteOnboardingWhenNeeded(window);

            var marker = Guid.NewGuid().ToString("N")[..8];

            var reminderTitle = $"Journey reminder {marker}";
            Navigate(window, "NavReminders", "RemindersPageTitle");
            Find(window, "RemindersTitle").AsTextBox().Enter(reminderTitle);
            Find(window, "RemindersSave").AsButton().Invoke();
            WaitForText(window, "RemindersStatusMessage", "reminder saved");
            SelectByName(window, "RemindersList", reminderTitle);
            Find(window, "RemindersComplete").AsButton().Invoke();
            WaitForText(window, "RemindersStatusMessage", "done le good job");

            var taskTitle = $"Journey task {marker}";
            Navigate(window, "NavTasksFocus", "TasksPageTitle");
            Find(window, "TasksTitle").AsTextBox().Enter(taskTitle);
            Find(window, "TasksSave").AsButton().Invoke();
            WaitForText(window, "TasksStatusMessage", "task saved");
            SelectByName(window, "TasksActiveList", taskTitle);
            Find(window, "TasksComplete").AsButton().Invoke();
            WaitForText(window, "TasksStatusMessage", "task done");

            // Run one real minute-long focus session to full completion (distinct from the
            // ended-early path SettingsNavigationTests already covers). "custom" is the only
            // preset whose minutes the NumberBox controls; the NumberBox's own Minimum is 1.
            Find(window, "FocusPreset").AsComboBox().Select("custom");
            Find(window, "FocusCustomDuration").AsTextBox().Enter("1");
            Find(window, "FocusStart").AsButton().Invoke();
            WaitForText(window, "FocusCurrent", "Focus is running");
            WaitUntil(
                () => Find(window, "FocusHistoryList").FindFirstDescendant(cf => cf.ByName("completed")) is not null,
                TimeSpan.FromSeconds(90));

            Navigate(window, "NavHome", "HomePageTitle");
            Find(window, "HomeCheckInNote").AsTextBox().Enter($"journey-check-in-{marker}");
            Find(window, "HomeSaveCheckIn").AsButton().Invoke();
            WaitForText(window, "HomeStatusMessage", "oki noted mwamwa");

            Navigate(window, "NavLoveNotes", "LoveNotesPageTitle");
            RevealFirstPendingRemoteNote(window);
            Assert.Equal(FixtureNoteText, Find(window, "LoveNotesOpened").AsTextBox().Text);

            Navigate(window, "NavAppearance", "AppearancePageTitle");
            Find(window, "AppearanceTheme").AsComboBox().Select(1);
            Find(window, "AppearanceReducedMotion").AsCheckBox().IsChecked = true;
            // Outfit persistence is gated behind a real device pairing
            // (CanPersistOutfit in AppearanceViewModel); this single-device journey never pairs,
            // so it stays disabled here exactly as SettingsNavigationTests already asserts for an
            // unpaired device. Faking pairing success to force it enabled would be a shortcut
            // around a real security boundary, so this journey only verifies the expected state.
            Assert.False(Find(window, "AppearanceOutfit").Properties.IsEnabled.ValueOrDefault);
            Find(window, "AppearanceSave").AsButton().Invoke();
            WaitForText(window, "AppearanceStatusMessage", "appearance saved");

            Navigate(window, "NavPrivacy", "PrivacyPageTitle");
            Find(window, "PrivacyBackup").AsButton().Invoke();
            WaitForText(window, "PrivacyStatusMessage", "backup created");

            automation.Dispose();
            try { application.Close(); }
            catch { application.Kill(); }

            application = Application.Launch(executable);
            using var automationAfterRestart = new UIA3Automation();
            var windowAfterRestart = application.GetMainWindow(automationAfterRestart)
                ?? throw new InvalidOperationException("The Dudu settings window did not reappear after restart.");
            WaitUntil(() => TryFind(windowAfterRestart, "NavHome") is not null, TimeSpan.FromSeconds(15));

            Navigate(windowAfterRestart, "NavTasksFocus", "TasksPageTitle");
            Assert.NotNull(Find(windowAfterRestart, "TasksCompletedList").FindFirstDescendant(cf => cf.ByName(taskTitle)));
            Assert.NotNull(Find(windowAfterRestart, "FocusHistoryList").FindFirstDescendant(cf => cf.ByName("completed")));

            Navigate(windowAfterRestart, "NavHome", "HomePageTitle");
            Assert.NotNull(Find(windowAfterRestart, "HomeCheckInHistory").FindFirstDescendant(
                cf => cf.ByName($"journey-check-in-{marker}")));

            Navigate(windowAfterRestart, "NavAppearance", "AppearancePageTitle");
            Assert.True(Find(windowAfterRestart, "AppearanceReducedMotion").AsCheckBox().IsChecked);

            Navigate(windowAfterRestart, "NavLoveNotes", "LoveNotesPageTitle");
            Assert.NotNull(Find(windowAfterRestart, "LoveNotesRemoteList")
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("DUDU_FIXTURE_NOTE", previousFixtureNote);
            if (application is not null)
            {
                try { application.Close(); }
                catch { application.Kill(); }
            }

            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void RevealFirstPendingRemoteNote(Window window)
    {
        AutomationElement? item = null;
        WaitUntil(() =>
        {
            item = Find(window, "LoveNotesRemoteList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
            return item is not null;
        });
        item!.Click();
        Find(window, "LoveNotesRevealSelected").AsButton().Invoke();
        WaitUntil(() => !string.IsNullOrWhiteSpace(Find(window, "LoveNotesOpened").AsTextBox().Text));
    }

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

    private static void Navigate(Window window, string navigationId, string pageTitleId)
    {
        Find(window, navigationId).AsButton().Invoke();
        WaitUntil(() => IsVisible(window, pageTitleId));
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

    private static void WaitUntil(Func<bool> condition) => WaitUntil(condition, TimeSpan.FromSeconds(15));

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * timeout.TotalSeconds);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the Dudu full journey to progress.");
            }

            Thread.Sleep(50);
        }
    }
}
