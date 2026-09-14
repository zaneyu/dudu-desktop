using Xunit;

namespace Dudu.UiTests;

/// <summary>
/// Windows-only accessibility coverage: every actionable settings control must have an
/// accessible name and be reachable from the keyboard, and the UI must not clip primary actions
/// at 200% text scaling. The executable is supplied by the Windows publish job so this project
/// remains buildable on non-Windows hosts without weakening the WinUI product target.
/// </summary>
[Collection(WindowsUiCollection.Name)]
public sealed class AccessibilityTests
{
    [Fact]
    public void Every_actionable_settings_control_has_name_and_keyboard_focus()
    {
        SkipUnlessWindowsUiAvailable();

        using var app = DuduUiFixture.LaunchFresh();
        foreach (var control in app.AllActionableControls())
        {
            Assert.False(string.IsNullOrWhiteSpace(control.Name), control.AutomationId);
            Assert.True(control.IsKeyboardFocusable, control.AutomationId);
        }
    }

    [Fact]
    public void Text_scaling_at_two_hundred_percent_has_no_clipped_primary_actions()
    {
        SkipUnlessWindowsUiAvailable();

        using var app = DuduUiFixture.LaunchFresh(textScalePercent: 200);
        app.VisitEverySettingsPage();

        Assert.Empty(app.FindClippedControls(minimumVisiblePercent: 95));
    }

    private static void SkipUnlessWindowsUiAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FlaUI accessibility tests require Windows UI Automation.");
        }

        var executable = Environment.GetEnvironmentVariable("DUDU_UI_TEST_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Assert.Skip("Set DUDU_UI_TEST_EXE to a Windows publish output to run UI automation.");
        }
    }
}
