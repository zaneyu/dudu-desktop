using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
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
        Find(window, "OnboardingNext").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingNext").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingNext").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingNext").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingSkipPairing").AsButton().Invoke();
        actions++;
        Find(window, "OnboardingComplete").AsButton().Invoke();
        actions++;

        Assert.True(actions < 20);
        Assert.NotNull(Find(window, "NavHome"));
    }

    private static AutomationElement Find(Window window, string automationId) =>
        window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
        ?? throw new InvalidOperationException($"Automation id '{automationId}' was not found.");
}
