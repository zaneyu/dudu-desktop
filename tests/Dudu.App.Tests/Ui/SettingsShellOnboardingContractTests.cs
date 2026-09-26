using System.Text.RegularExpressions;
using Xunit;

namespace Dudu.App.Tests.Ui;

/// <summary>Text-level regressions for the settings shell, Home and the first-run
/// flow. XAML is not compiled on non-Windows hosts, so these pin the specific
/// markup/code-behind fixes the way <see cref="XamlContractTests"/> does.</summary>
public sealed class SettingsShellOnboardingContractTests
{
    [Fact]
    public void Compact_navigation_items_all_have_icons()
    {
        // PaneDisplayMode="LeftCompact" shows only icons until the pane opens.
        var shell = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml");
        Assert.Contains("PaneDisplayMode=\"LeftCompact\"", shell);

        var items = Regex.Matches(
            shell,
            "<NavigationViewItem\\s[^>]*?(/?)>(.*?)(?=<NavigationViewItem\\s|</NavigationView.MenuItems>)",
            RegexOptions.Singleline);
        Assert.Equal(7, items.Count);
        foreach (Match item in items)
        {
            Assert.True(item.Groups[1].Value.Length == 0, $"Navigation item has no icon: {item.Value}");
            Assert.Contains("<NavigationViewItem.Icon>", item.Groups[2].Value);
            Assert.Matches("<FontIcon Glyph=\"&#x[0-9A-F]{4};\" />", item.Groups[2].Value);
        }
    }

    [Fact]
    public void Shell_does_not_preselect_home_before_the_pending_destination_is_known()
    {
        var code = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs");
        var constructor = Slice(code, "public SettingsWindow(CompanionSettingsContext context)", "\n    }");

        Assert.DoesNotContain("RootNavigation.SelectedItem =", constructor);
    }

    [Fact]
    public void Companion_status_is_neutral_and_announced_with_its_own_text()
    {
        var shell = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml");
        var code = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs");
        var status = Regex.Match(shell, "<TextBlock x:Name=\"DuduCompanionStatus\"[^>]*>").Value;

        Assert.NotEmpty(status);
        // It reports failures ("dudu couldnt do that yet") as well as confirmations.
        Assert.DoesNotContain("SuccessBrush", status);
        Assert.Contains("AutomationProperties.SetName(DuduCompanionStatus", code);
        Assert.Contains("AutomationEvents.LiveRegionChanged", code);
        Assert.Single(Regex.Matches(code, "DuduCompanionStatus\\.Text\\s*="));
        Assert.Contains("HomeViewModel.DescribePetState(presentation.State)", code);
        Assert.DoesNotContain("presentation.State.ToString().ToLowerInvariant()", code);
    }

    [Fact]
    public void Onboarding_completion_handoff_cannot_throw_back_into_the_onboarding_page()
    {
        var code = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs");
        var handoff = Slice(code, "private void OnboardingCompleted()", "\n    }");

        Assert.Contains("try", handoff);
        Assert.Contains("catch (Exception exception)", handoff);
        Assert.Contains("RuntimeApplyError", handoff);
        Assert.Contains("FocusSelectedNavigationItem();", handoff);
    }

    [Fact]
    public void Primary_button_style_keeps_the_implicit_button_metrics()
    {
        var controls = Read("src", "Dudu.App", "Themes", "Controls.xaml");
        var primary = Slice(controls, "<Style x:Key=\"PrimaryButtonStyle\"", "</Style>");

        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"40\" />", primary);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"16,8\" />", primary);
        Assert.Contains("<Setter Property=\"FontFamily\" Value=\"Segoe UI Variable\" />", primary);
    }

    [Fact]
    public void Home_wraps_text_instead_of_scrolling_sideways_and_shows_local_check_in_times()
    {
        var home = Read("src", "Dudu.App", "Pages", "HomePage.xaml");

        Assert.Contains("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\"", home);
        Assert.DoesNotContain("{x:Bind CreatedUtc}", home);
        Assert.DoesNotContain("{x:Bind Choice}", home);
        Assert.Contains("viewmodels:HomeViewModel.FormatCheckInTime(CreatedUtc)", home);
        Assert.Contains("viewmodels:HomeViewModel.FormatCheckInChoice(Choice)", home);
        Assert.Contains("xmlns:viewmodels=\"using:Dudu.App.ViewModels\"", home);
    }

    [Fact]
    public void Home_dynamic_text_exposes_its_value_as_the_accessible_name()
    {
        // A static AutomationProperties.Name overrides a TextBlock's text for UIA, so
        // Narrator read "check-in summary" instead of "1 optional check-in ...".
        var home = Read("src", "Dudu.App", "Pages", "HomePage.xaml");
        var bound = Regex.Matches(
            home,
            "<TextBlock\\s[^>]*Text=\"\\{x:Bind ViewModel\\.(\\w+), Mode=OneWay\\}\"[^>]*AutomationProperties\\.AutomationId=\"[^\"]+\"[^>]*>");

        Assert.True(bound.Count >= 9, $"expected the Home status texts, found {bound.Count}");
        foreach (Match tag in bound)
        {
            Assert.Contains(
                $"AutomationProperties.Name=\"{{x:Bind ViewModel.{tag.Groups[1].Value}, Mode=OneWay}}\"",
                tag.Value);
        }

        var code = Read("src", "Dudu.App", "Pages", "HomePage.xaml.cs");
        Assert.DoesNotContain("HomeActionStatus.Text =", code);
        Assert.DoesNotContain("CountdownTargetValidation.Text =", code);
        Assert.DoesNotContain("StartupRecoveryMessage.Text =", code);
    }

    [Fact]
    public void Home_countdown_target_box_follows_the_view_model_after_a_save()
    {
        var code = Read("src", "Dudu.App", "Pages", "HomePage.xaml.cs");

        Assert.Contains("ViewModel.PropertyChanged += ViewModel_PropertyChanged;", code);
        var handler = Slice(code, "private void ViewModel_PropertyChanged(", "\n    }");
        Assert.Contains("nameof(HomeViewModel.CountdownTargetUtc)", handler);
        Assert.Contains("HomeViewModel.CountdownTargetTextMatches(CountdownTargetBox.Text, ViewModel.CountdownTargetUtc)", handler);
        Assert.Contains("CountdownTargetBox.Text =", handler);
    }

    [Fact]
    public void Onboarding_announces_progress_and_errors_with_their_actual_text()
    {
        var onboarding = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml");
        var code = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml.cs");

        Assert.Matches("<TextBlock x:Name=\"OnboardingProgress\"[^>]*AutomationProperties.LiveSetting=\"Polite\"", onboarding);
        Assert.Matches("<TextBlock x:Name=\"OnboardingValidationMessage\"[^>]*AutomationProperties.LiveSetting=\"Assertive\"", onboarding);
        Assert.Matches("<TextBlock x:Name=\"OnboardingStatusMessage\"[^>]*AutomationProperties.LiveSetting=\"Polite\"", onboarding);
        Assert.DoesNotContain("AutomationProperties.Name=\"pairing is optional\"", onboarding);
        Assert.DoesNotContain("encrypted notes its optional", onboarding);
        foreach (var element in new[] { "OnboardingProgress", "OnboardingValidationMessage", "PairingStatus", "StartupRecoveryMessage", "OnboardingStatusMessage" })
        {
            Assert.DoesNotMatch($"\\b{element}\\.Text\\s*=", code);
        }

        Assert.Contains("AutomationProperties.SetName(block, block.Text);", code);
    }

    [Fact]
    public void Recommended_buttons_capture_typed_choices_before_rewriting_the_controls()
    {
        // The UI journey types a name and then presses "use recommended defaults";
        // syncing controls from the draft first wiped the typed name.
        var code = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml.cs");

        foreach (var (handler, call) in new[]
        {
            ("private async void RecommendedDefaultsButton_Click(", "await _viewModel.AcceptRecommendedDefaultsAsync();"),
            ("private async void RecommendedPlacementButton_Click(", "await _viewModel.UseRecommendedPlacementAsync();"),
        })
        {
            var body = Slice(code, handler, "\n    }");
            var sync = body.IndexOf("SyncDraftFromControls();", StringComparison.Ordinal);
            var accept = body.IndexOf(call, StringComparison.Ordinal);
            Assert.True(sync >= 0 && accept > sync, $"{handler} must sync the draft before {call}");
            Assert.Contains("catch (Exception exception)", body);
        }
    }

    [Fact]
    public void Onboarding_ignores_slider_events_raised_while_the_page_builds_itself()
    {
        var code = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml.cs");
        var onboarding = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml");

        Assert.Contains("private bool _suppressControlEvents = true;", code);
        var constructor = Slice(code, "public OnboardingPage(", "\n    }");
        Assert.True(
            constructor.IndexOf("InitializeComponent();", StringComparison.Ordinal)
                < constructor.IndexOf("_suppressControlEvents = false;", StringComparison.Ordinal),
            "Slider events must stay suppressed through InitializeComponent and the first sync.");
        var handler = Slice(code, "private async void PlacementScaleSlider_ValueChanged(", "\n    }");
        Assert.Contains("if (_suppressControlEvents) return;", handler);
        Assert.True(
            handler.IndexOf("if (_suppressControlEvents) return;", StringComparison.Ordinal)
                < handler.IndexOf("_viewModel.PlacementScale = args.NewValue;", StringComparison.Ordinal));
        Assert.Contains("IsThumbToolTipEnabled=\"False\"", onboarding);
        Assert.Contains("AutomationProperties.AutomationId=\"OnboardingPetScaleValue\"", onboarding);
    }

    [Fact]
    public void Onboarding_transitions_are_single_flight_and_keep_keyboard_focus()
    {
        var code = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml.cs");

        var complete = Slice(code, "private async void CompleteButton_Click(", "\n    }");
        Assert.Contains("if (_busy || _completionRaised) return;", complete);
        Assert.Contains("SetBusy(true);", complete);
        Assert.Contains("RaiseCompleted();", complete);
        Assert.DoesNotContain("_completed();", complete);

        var next = Slice(code, "private async Task MoveNextAsync()", "\n    }");
        Assert.Contains("if (_busy || _completionRaised) return;", next);
        Assert.Contains("FocusLater(CompleteButton);", next);

        var back = Slice(code, "private void BackButton_Click(", "\n    }");
        Assert.Contains("FocusLater(NextButton);", back);

        var raise = Slice(code, "private void RaiseCompleted()", "\n    }");
        Assert.Contains("if (_completionRaised) return;", raise);
    }

    [Fact]
    public void Onboarding_routes_free_text_through_view_model_validation()
    {
        var code = Read("src", "Dudu.App", "Pages", "OnboardingPage.xaml.cs");

        Assert.Contains("_viewModel.TrySetQuietHoursText(QuietStartBox.Text, QuietEndBox.Text);", code);
        Assert.Contains("_viewModel.SetLocalNoteDailyLimitInput(NoteLimitBox.Value);", code);
        Assert.DoesNotContain("TimeOnly.TryParse(QuietStartBox.Text", code);
        Assert.DoesNotContain("(int)Math.Round(NoteLimitBox.Value)", code);
    }

    [Theory]
    [InlineData("OnboardingPage", "Pages")]
    [InlineData("SettingsWindow", "Windows")]
    public void Every_named_element_used_from_code_behind_has_a_compile_stub(string className, string folder)
    {
        var xaml = Read("src", "Dudu.App", folder, $"{className}.xaml");
        var code = Read("src", "Dudu.App", folder, $"{className}.xaml.cs");
        var stubs = Read("src", "Dudu.App", "XamlCompileStubs.cs");
        var stubBody = Slice(stubs, $"public sealed partial class {className}", "private void InitializeComponent()");

        foreach (Match name in Regex.Matches(xaml, "x:Name=\"([^\"]+)\""))
        {
            var element = name.Groups[1].Value;
            if (!Regex.IsMatch(code, $"\\b{element}\\b")) continue;
            Assert.Matches($"private \\w+ {element} = null!;", stubBody);
        }
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts])).Replace("\r\n", "\n");

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

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
