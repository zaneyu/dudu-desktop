using System.Text.RegularExpressions;
using Xunit;

namespace Dudu.App.Tests.Ui;

/// <summary>Text-level regressions for fixes that span the feature pages: text wraps
/// instead of scrolling sideways at high scaling, dynamic text is what screen readers
/// announce, and re-opening settings restores and refreshes the window. XAML is not
/// compiled on non-Windows hosts, so these pin the markup/code the way
/// <see cref="XamlContractTests"/> does.</summary>
public sealed class CrossPageLayoutAccessibilityContractTests
{
    public static TheoryData<string> FeaturePages => new()
    {
        "ConnectionPage",
        "LoveNotesPage",
        "PrivacyDataPage",
        "RemindersPage",
        "TasksFocusPage",
    };

    [Theory]
    [MemberData(nameof(FeaturePages))]
    public void Page_wraps_instead_of_scrolling_sideways(string page)
    {
        var xaml = Read("src", "Dudu.App", "Pages", $"{page}.xaml");

        Assert.Contains("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\"", xaml);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", xaml);

        // With sideways scrolling gone, anything laid out in a horizontal StackPanel gets
        // clipped at 200% scaling: a horizontal StackPanel measures children with infinite
        // width, so a wrapping TextBlock never wraps and a button row runs off the edge.
        foreach (Match panel in Regex.Matches(
            xaml,
            "<StackPanel[^>]*Orientation=\"Horizontal\"[^>]*>(.*?)</StackPanel>",
            RegexOptions.Singleline))
        {
            Assert.DoesNotContain("<Button", panel.Groups[1].Value);
            Assert.DoesNotContain("TextWrapping=\"Wrap\"", panel.Groups[1].Value);
        }

        // Fixed widths on inputs would also clip now; only the tiny progress ring may.
        foreach (Match sized in Regex.Matches(xaml, "<(\\w+)\\s[^>]*\\sWidth=\"\\d+\""))
        {
            Assert.Equal("ProgressRing", sized.Groups[1].Value);
        }
    }

    [Theory]
    [MemberData(nameof(FeaturePages))]
    public void Status_and_error_rows_let_their_text_wrap(string page)
    {
        var xaml = Read("src", "Dudu.App", "Pages", $"{page}.xaml");

        foreach (var flag in new[] { "HasStatus", "HasError" })
        {
            var row = Regex.Match(
                xaml,
                $"<Grid ColumnSpacing=\"6\" Visibility=\"\\{{x:Bind ViewModel\\.{flag}, Mode=OneWay\\}}\">(.*?)</Grid>\\n",
                RegexOptions.Singleline);
            Assert.True(row.Success, $"{page}: {flag} row is not a wrapping Grid");
            Assert.Contains("<ColumnDefinition Width=\"*\" />", row.Value);
            Assert.Matches("<TextBlock Grid.Column=\"1\"[^>]*TextWrapping=\"Wrap\"", row.Value);
        }
    }

    [Theory]
    [MemberData(nameof(FeaturePages))]
    public void Bound_dynamic_text_is_also_the_accessible_name(string page)
    {
        // A fixed AutomationProperties.Name overrides a TextBlock's text for UIA, so
        // Narrator read "delete task confirmation message" instead of the prompt itself.
        var xaml = Read("src", "Dudu.App", "Pages", $"{page}.xaml");
        var bound = Regex.Matches(
            xaml,
            "<TextBlock\\s[^>]*Text=\"\\{x:Bind ViewModel\\.(\\w+), Mode=OneWay\\}\"[^>]*AutomationProperties\\.AutomationId=\"[^\"]+\"[^>]*>");

        Assert.True(bound.Count >= 2, $"{page}: expected bound status texts, found {bound.Count}");
        foreach (Match tag in bound)
        {
            Assert.Contains(
                $"AutomationProperties.Name=\"{{x:Bind ViewModel.{tag.Groups[1].Value}, Mode=OneWay}}\"",
                tag.Value);
        }
    }

    [Theory]
    [InlineData("ConnectionPage", "ConnectionAvailability")]
    [InlineData("ConnectionPage", "ConnectionPairingCode")]
    [InlineData("ConnectionPage", "ConnectionCodeExpiry")]
    [InlineData("ConnectionPage", "ConnectionSessionCount")]
    [InlineData("LoveNotesPage", "LoveNotesDailyLimit")]
    [InlineData("LoveNotesPage", "LoveNotesPendingCount")]
    [InlineData("RemindersPage", "RemindersLocalTimeValidation")]
    [InlineData("TasksFocusPage", "TaskDueValidation")]
    [InlineData("TasksFocusPage", "FocusCurrent")]
    public void Code_behind_text_keeps_the_accessible_name_in_step(string page, string element)
    {
        var code = Read("src", "Dudu.App", "Pages", $"{page}.xaml.cs");

        // Every direct write is followed by a matching SetName, or the text goes through
        // a SetText(block, text) helper that sets both.
        var writes = Regex.Matches(code, $@"\b{element}\.Text\s*=");
        var named = Regex.Matches(code, $@"\b{element}\.Text = [^;]+;\n\s*AutomationProperties\.SetName\({element}, ");
        var viaHelper = Regex.Matches(code, $@"\bSetText\({element}, ");

        Assert.Equal(writes.Count, named.Count);
        Assert.True(writes.Count + viaHelper.Count > 0, $"{page}.{element} is never written");
        if (viaHelper.Count > 0)
        {
            Assert.Matches(@"block\.Text = text;\n\s*AutomationProperties\.SetName\(block, text\);", code);
        }
    }

    [Theory]
    [InlineData("RemindersPage", "RemindersLocalTimeValidation", "SetValidationText")]
    [InlineData("TasksFocusPage", "TaskDueValidation", "SetDueValidationText")]
    public void Validation_text_is_only_written_through_the_naming_helper(string page, string element, string helper)
    {
        var code = Read("src", "Dudu.App", "Pages", $"{page}.xaml.cs");
        var helperBody = Slice(code, $"private void {helper}(string text)", "\n    }");

        Assert.Single(Regex.Matches(code, $"{element}\\.Text\\s*="));
        Assert.Contains($"{element}.Text = text;", helperBody);
        Assert.Contains($"AutomationProperties.SetName({element},", helperBody);
    }

    [Fact]
    public void Reopening_settings_restores_a_minimized_window_and_refreshes_the_page()
    {
        var app = Read("src", "Dudu.App", "App.xaml.cs");
        var open = Slice(app, "private void OpenSettingsCore(StartupSettingsService startup)", "\n    }\n");
        var restore = Slice(app, "private static void RestoreIfMinimized(Window window)", "\n    }\n");

        var reopening = open.IndexOf("if (reopening)", StringComparison.Ordinal);
        var activate = open.LastIndexOf("_settingsWindow.Activate();", StringComparison.Ordinal);
        Assert.True(open.IndexOf("var reopening = _settingsWindow is not null;", StringComparison.Ordinal) >= 0);
        Assert.True(reopening > 0 && activate > reopening, "restore/refresh must run before Activate()");
        Assert.Contains("RestoreIfMinimized(_settingsWindow);", open[reopening..activate]);
        Assert.Contains("RefreshCurrentPage();", open[reopening..activate]);

        Assert.Contains("OverlappedPresenter", restore);
        Assert.Contains("OverlappedPresenterState.Minimized", restore);
        Assert.Contains("presenter.Restore();", restore);
        Assert.Contains("catch (Exception exception)", restore);

        var shell = Read("src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs");
        var refresh = Slice(shell, "public async void RefreshCurrentPage()", "\n    }\n");
        Assert.Contains("if (!_featurePagesInitialized) return;", refresh);
        foreach (var page in new[] { "HomePage", "RemindersPage", "TasksFocusPage", "LoveNotesPage", "ConnectionPage" })
        {
            Assert.Contains($"{page} {{ IsLoaded: true }} page => () => page.ViewModel.RefreshAsync()", refresh);
        }

        // Appearance's refresh would overwrite unsaved edits with stored preferences.
        Assert.DoesNotContain("AppearancePage", refresh);
        Assert.Contains("catch (Exception exception)", refresh);
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

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
