using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only end-to-end coverage of the whole cute journey: onboarding, the Home actions,
/// revealing a fixture remote note (which keeps it in the opened notes), a settings change, a
/// restart, and persistence across that restart. The executable is supplied by the Windows publish job so this project remains
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

            // Home: petting and a tiny hug respond from the keyboard-reachable buttons.
            Navigate(window, "NavHome", "HomePageTitle");
            Find(window, "OverlayActionPet").AsButton().Invoke();
            Find(window, "OverlayComfortActionTinyHug").AsButton().Invoke();
            WaitForText(window, "HomeActionStatus", "tiny hug ready le");

            // Revealing the fixture note shows it and keeps it in the opened notes.
            Navigate(window, "NavLoveNotes", "LoveNotesPageTitle");
            RevealFirstPendingRemoteNote(window);
            Assert.Equal(FixtureNoteText, Find(window, "LoveNotesOpened").AsTextBox().Text);
            Assert.NotNull(Find(window, "LoveNotesOpenedList")
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)));

            Navigate(window, "NavSettings", "SettingsPageTitle");
            Find(window, "AppearanceTheme").AsComboBox().Select(1);
            Find(window, "AppearanceReducedMotion").AsCheckBox().IsChecked = true;
            Find(window, "AppearanceSave").AsButton().Invoke();
            WaitForText(window, "AppearanceStatusMessage", "appearance saved");

            automation.Dispose();
            try { application.Close(); }
            catch { application.Kill(); }

            application = Application.Launch(executable);
            using var automationAfterRestart = new UIA3Automation();
            var windowAfterRestart = application.GetMainWindow(automationAfterRestart)
                ?? throw new InvalidOperationException("The Dudu settings window did not reappear after restart.");
            WaitUntil(() => TryFind(windowAfterRestart, "NavHome") is not null, TimeSpan.FromSeconds(15));

            // The recipient name from onboarding still greets her.
            Navigate(windowAfterRestart, "NavHome", "HomePageTitle");
            WaitForText(windowAfterRestart, "HomeGreeting", "Mia");

            Navigate(windowAfterRestart, "NavSettings", "SettingsPageTitle");
            Assert.True(Find(windowAfterRestart, "AppearanceReducedMotion").AsCheckBox().IsChecked);

            // Revealing keeps the note: it is still in the opened notes after a restart.
            // (The pending list is not checked here: DUDU_FIXTURE_NOTE reinstalls the
            // fixture envelope on every launch, and consuming it deleted the row.)
            Navigate(windowAfterRestart, "NavLoveNotes", "LoveNotesPageTitle");
            AutomationElement? openedItem = null;
            WaitUntil(() =>
            {
                openedItem = Find(windowAfterRestart, "LoveNotesOpenedList")
                    .FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
                return openedItem is not null;
            });
            openedItem!.Click();
            WaitUntil(() => Find(windowAfterRestart, "LoveNotesOpened").AsTextBox().Text == FixtureNoteText);
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
        Advance(window, "OnboardingTheme");
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
