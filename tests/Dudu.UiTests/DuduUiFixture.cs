using System.Diagnostics;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Dudu.UiTests;

/// <summary>
/// One actionable (invokable/focusable) control found while walking every settings page, with
/// just the properties <see cref="AccessibilityTests"/> needs to assert against.
/// </summary>
internal sealed record ActionableControl(string AutomationId, string Name, bool IsKeyboardFocusable);

/// <summary>
/// Shared Windows UI Automation launch/teardown for <see cref="AccessibilityTests"/>. Launches a
/// fresh <c>Dudu.App</c> process against an isolated <c>DUDU_DATA_ROOT</c>, completes onboarding
/// with the recommended defaults so every settings page is reachable, and restores the
/// environment on <see cref="Dispose"/>. Requires <c>DUDU_UI_TEST_EXE</c> to point at a Windows
/// publish output; callers check that themselves via <c>Assert.Skip</c> before calling
/// <see cref="LaunchFresh"/> so the skip reason stays test-local.
/// </summary>
internal sealed class DuduUiFixture : IDisposable
{
    private static readonly Destination[] SettingsPages =
    [
        new("NavHome", "HomePageTitle"),
        new("NavReminders", "RemindersPageTitle"),
        new("NavTasksFocus", "TasksPageTitle"),
        new("NavLoveNotes", "LoveNotesPageTitle"),
        new("NavAppearance", "AppearancePageTitle"),
        new("NavConnection", "ConnectionPageTitle"),
        new("NavPrivacy", "PrivacyPageTitle"),
    ];

    private readonly Application _application;
    private readonly UIA3Automation _automation;
    private readonly string _dataRoot;
    private readonly string? _previousDataRoot;
    private readonly string? _previousTextScale;
    private bool _disposed;

    private DuduUiFixture(
        Application application,
        UIA3Automation automation,
        Window window,
        string dataRoot,
        string? previousDataRoot,
        string? previousTextScale)
    {
        _application = application;
        _automation = automation;
        Window = window;
        _dataRoot = dataRoot;
        _previousDataRoot = previousDataRoot;
        _previousTextScale = previousTextScale;
    }

    public Window Window { get; }

    /// <summary>
    /// Launches a fresh instance. <paramref name="textScalePercent"/> is forwarded as
    /// <c>DUDU_TEXT_SCALE_PERCENT</c>, which the app applies as a
    /// <c>ControlContentThemeFontSize</c> resource-override multiplier (see
    /// <c>App.xaml.cs</c>) rather than a <c>ScaleTransform</c>, so controls report their real,
    /// larger-font layout bounds to UI Automation instead of a visually stretched bitmap.
    /// </summary>
    public static DuduUiFixture LaunchFresh(int textScalePercent = 100)
    {
        var executable = Environment.GetEnvironmentVariable("DUDU_UI_TEST_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException(
                "Set DUDU_UI_TEST_EXE to a Windows publish output to run UI automation.");
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), "dudu-ui-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var previousDataRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        var previousTextScale = Environment.GetEnvironmentVariable("DUDU_TEXT_SCALE_PERCENT");
        Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", dataRoot);
        Environment.SetEnvironmentVariable(
            "DUDU_TEXT_SCALE_PERCENT",
            textScalePercent.ToString(CultureInfo.InvariantCulture));

        Application application;
        try
        {
            application = Application.Launch(executable);
        }
        catch
        {
            RestoreEnvironment(previousDataRoot, previousTextScale);
            throw;
        }

        var automation = new UIA3Automation();
        try
        {
            var window = application.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The Dudu settings window did not appear.");
            CompleteOnboardingWhenNeeded(window);
            return new DuduUiFixture(application, automation, window, dataRoot, previousDataRoot, previousTextScale);
        }
        catch
        {
            automation.Dispose();
            try { application.Close(); } catch { application.Kill(); }
            RestoreEnvironment(previousDataRoot, previousTextScale);
            throw;
        }
    }

    /// <summary>
    /// Walks every settings destination and returns every descendant whose UIA control type is
    /// normally actionable from the keyboard (buttons, text inputs, checkboxes, combo boxes,
    /// list items, radio buttons).
    /// </summary>
    public IReadOnlyList<ActionableControl> AllActionableControls()
    {
        var results = new List<ActionableControl>();
        var seenAutomationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in SettingsPages)
        {
            Navigate(destination);
            foreach (var element in Window.FindAllDescendants())
            {
                if (!IsActionableControlType(element.Properties.ControlType.ValueOrDefault))
                {
                    continue;
                }

                var automationId = element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                if (automationId.Length == 0 || !seenAutomationIds.Add(automationId))
                {
                    continue;
                }

                results.Add(new ActionableControl(
                    automationId,
                    element.Properties.Name.ValueOrDefault ?? string.Empty,
                    element.Properties.IsKeyboardFocusable.ValueOrDefault));
            }
        }

        return results;
    }

    /// <summary>Navigates to every settings destination in turn, leaving each one rendered long
    /// enough for layout to settle before the caller inspects it.</summary>
    public void VisitEverySettingsPage()
    {
        foreach (var destination in SettingsPages)
        {
            Navigate(destination);
        }
    }

    /// <summary>
    /// Returns the automation ids of primary action controls (buttons) that are either fully
    /// offscreen or whose bounding rectangle falls outside the window's client bounds by more
    /// than <c>100 - minimumVisiblePercent</c>%. Call after <see cref="VisitEverySettingsPage"/>.
    /// </summary>
    public IReadOnlyList<string> FindClippedControls(int minimumVisiblePercent)
    {
        var clipped = new List<string>();
        var windowBounds = Window.BoundingRectangle;
        foreach (var destination in SettingsPages)
        {
            Navigate(destination);
            foreach (var element in Window.FindAllDescendants())
            {
                if (element.Properties.ControlType.ValueOrDefault != ControlType.Button)
                {
                    continue;
                }

                var automationId = element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                if (automationId.Length == 0)
                {
                    continue;
                }

                if (element.Properties.IsOffscreen.ValueOrDefault)
                {
                    clipped.Add(automationId);
                    continue;
                }

                var bounds = element.BoundingRectangle;
                var totalArea = (double)Math.Max(1, bounds.Width * bounds.Height);
                var visibleWidth = Math.Max(
                    0,
                    Math.Min(bounds.Right, windowBounds.Right) - Math.Max(bounds.Left, windowBounds.Left));
                var visibleHeight = Math.Max(
                    0,
                    Math.Min(bounds.Bottom, windowBounds.Bottom) - Math.Max(bounds.Top, windowBounds.Top));
                var visiblePercent = (visibleWidth * visibleHeight) * 100.0 / totalArea;
                if (visiblePercent < minimumVisiblePercent)
                {
                    clipped.Add(automationId);
                }
            }
        }

        return clipped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _automation.Dispose();
        try { _application.Close(); }
        catch { _application.Kill(); }
        RestoreEnvironment(_previousDataRoot, _previousTextScale);
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    private static void RestoreEnvironment(string? previousDataRoot, string? previousTextScale)
    {
        Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previousDataRoot);
        Environment.SetEnvironmentVariable("DUDU_TEXT_SCALE_PERCENT", previousTextScale);
    }

    private static bool IsActionableControlType(ControlType controlType) => controlType switch
    {
        ControlType.Button => true,
        ControlType.CheckBox => true,
        ControlType.ComboBox => true,
        ControlType.Edit => true,
        ControlType.RadioButton => true,
        ControlType.ListItem => true,
        _ => false,
    };

    private void Navigate(Destination destination)
    {
        Window.FindFirstDescendant(cf => cf.ByAutomationId(destination.NavigationId))?.Click();
        WaitUntil(() => IsVisible(destination.PageTitleId));
    }

    private bool IsVisible(string automationId)
    {
        var element = Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        return element is not null && !element.Properties.IsOffscreen.ValueOrDefault;
    }

    private void WaitUntil(Func<bool> condition)
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

    private static void CompleteOnboardingWhenNeeded(Window window)
    {
        if (window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingRecipientName")) is null)
        {
            return;
        }

        window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingRecipientName"))!.AsTextBox().Enter("Mia");
        window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingRecommendedDefaults"))!.AsButton().Invoke();

        foreach (var visibleControlId in new[]
        {
            "OnboardingTheme",
            "OnboardingQuietHoursEnabled",
            "OnboardingHydrationReminders",
            "OnboardingPlacementStep",
            "OnboardingSkipPairing",
        })
        {
            window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingNext"))!.AsButton().Invoke();
            var deadline = Stopwatch.GetTimestamp() +
                (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(15).TotalSeconds);
            while (window.FindFirstDescendant(cf => cf.ByAutomationId(visibleControlId)) is null
                || window.FindFirstDescendant(cf => cf.ByAutomationId(visibleControlId))!
                    .Properties.IsOffscreen.ValueOrDefault)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new TimeoutException("Timed out waiting for the onboarding transition.");
                }

                Thread.Sleep(50);
            }
        }

        window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingSkipPairing"))!.AsButton().Invoke();
        window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingComplete"))!.AsButton().Invoke();

        var homeDeadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(15).TotalSeconds);
        while (window.FindFirstDescendant(cf => cf.ByAutomationId("NavHome")) is null)
        {
            if (Stopwatch.GetTimestamp() >= homeDeadline)
            {
                throw new TimeoutException("Timed out waiting for onboarding to reach home.");
            }

            Thread.Sleep(50);
        }
    }

    private sealed record Destination(string NavigationId, string PageTitleId);
}
