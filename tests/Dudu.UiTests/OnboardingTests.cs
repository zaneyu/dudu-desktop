using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System.Diagnostics;
using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only smoke coverage for the user-facing onboarding contract. The
/// executable is supplied by the Windows publish job so this project remains
/// buildable on non-Windows hosts without weakening the WinUI product target.
/// </summary>
public sealed class OnboardingTests
{
    [Fact]
    public void Recommended_onboarding_reaches_home_in_fewer_than_twenty_actions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FlaUI onboarding tests require Windows UI Automation.");
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
        var actions = 0;

        Find(window, "OnboardingRecipientName").AsTextBox().Enter("Mia");
        actions++;
        Find(window, "OnboardingRecommendedDefaults").AsButton().Invoke();
        actions++;
        // Recipient -> Appearance -> Quiet Hours -> Reminders -> Placement.
        Advance(
            window,
            ref actions,
            "OnboardingTheme",
            "OnboardingQuietHoursEnabled",
            "OnboardingHydrationReminders",
            "OnboardingPlacementStep");
        var overlay = WaitForOverlay(automation);
        Assert.False(overlay.Properties.IsOffscreen.ValueOrDefault);

        // Placement -> Pairing, then skip the optional pairing step.
        Advance(window, ref actions, "OnboardingSkipPairing");
        Find(window, "OnboardingSkipPairing").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingComplete").AsButton().Invoke();
        actions++;

        Assert.True(actions < 20);
        WaitUntil(() => TryFind(window, "NavHome") is not null);
        Assert.False(WaitForOverlay(automation).Properties.IsOffscreen.ValueOrDefault);
    }

    private static void Advance(Window window, ref int actions, params string[] visibleControlIds)
    {
        foreach (var visibleControlId in visibleControlIds)
        {
            Find(window, "OnboardingNext").AsButton().Invoke();
            actions++;
            WaitUntil(() => IsVisible(window, visibleControlId));
        }
    }

    private static AutomationElement WaitForOverlay(UIA3Automation automation)
    {
        AutomationElement? overlay = null;
        WaitUntil(() =>
        {
            overlay = automation.GetDesktop().FindFirstDescendant(
                cf => cf.ByClassName("Dudu.DesktopCompanion.PetOverlay.v1"));
            return overlay is not null;
        });
        return overlay!;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(5).TotalSeconds);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the onboarding transition.");
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
}
