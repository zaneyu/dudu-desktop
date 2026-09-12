using Xunit;
using System.Text.RegularExpressions;

namespace Dudu.App.Tests.Ui;

public sealed class XamlContractTests
{
    [Fact]
    public void Onboarding_and_primary_button_xaml_use_valid_accessible_contracts()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "OnboardingPage.xaml"));
        var controls = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Controls.xaml"));
        var colors = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Colors.xaml"));
        var stubs = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "XamlCompileStubs.cs"));

        Assert.Contains("SmallChange=\"0.1\"", onboarding);
        Assert.DoesNotContain("StepFrequency=", onboarding);
        Assert.Contains("Foreground=\"{ThemeResource PrimaryButtonForegroundBrush}\"", controls);
        Assert.Contains("SystemColorHighlightTextColor", colors);
        Assert.Contains("private StackPanel StartupRecoveryPanel", stubs);
        Assert.Contains("private TextBlock StartupRecoveryMessage", stubs);
    }

    [Fact]
    public void Settings_shell_maps_exactly_seven_native_destinations()
    {
        var root = FindRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs"));

        Assert.Equal(7, Regex.Matches(settings, "<NavigationViewItem ").Count);
        foreach (var item in new[]
        {
            ("Home", "home", "NavHome"),
            ("Reminders", "reminders", "NavReminders"),
            ("Tasks and Focus", "tasks", "NavTasksFocus"),
            ("Love Notes", "notes", "NavLoveNotes"),
            ("Appearance", "appearance", "NavAppearance"),
            ("Connection", "connection", "NavConnection"),
            ("Privacy and Data", "privacy", "NavPrivacy"),
        })
        {
            Assert.Contains($"Content=\"{item.Item1}\" Tag=\"{item.Item2}\" AutomationProperties.AutomationId=\"{item.Item3}\"", settings);
        }

        foreach (var pageType in new[]
        {
            "HomePage", "RemindersPage", "TasksFocusPage", "LoveNotesPage",
            "AppearancePage", "ConnectionPage", "PrivacyDataPage",
        })
        {
            Assert.Contains($"private readonly {pageType} _", code);
            Assert.Contains($"new {pageType}", code);
        }

        Assert.Contains("\"home\" => _homePage", code);
        Assert.Contains("\"reminders\" => _remindersPage", code);
        Assert.Contains("\"tasks\" => _tasksFocusPage", code);
        Assert.Contains("\"notes\" => _loveNotesPage", code);
        Assert.Contains("\"appearance\" => _appearancePage", code);
        Assert.Contains("\"connection\" => _connectionPage", code);
        Assert.Contains("\"privacy\" => _privacyDataPage", code);
    }

    [Fact]
    public void Settings_pages_keep_accessible_controls_and_banned_patterns_out()
    {
        var root = FindRepositoryRoot();
        var pagesDirectory = Path.Combine(root, "src", "Dudu.App", "Pages");
        var pageNames = new[]
        {
            "HomePage", "RemindersPage", "TasksFocusPage", "LoveNotesPage",
            "AppearancePage", "ConnectionPage", "PrivacyDataPage",
        };
        var pageFiles = pageNames
            .Select(page => Path.Combine(pagesDirectory, $"{page}.xaml"))
            .ToArray();

        Assert.Equal(7, pageFiles.Length);
        var allPages = string.Join("\n", pageFiles.Select(File.ReadAllText));
        var stubs = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "XamlCompileStubs.cs"));
        var pageContracts = new Dictionary<string, string>
        {
            ["HomePage"] = "HomeViewModel",
            ["RemindersPage"] = "RemindersViewModel",
            ["TasksFocusPage"] = "TasksFocusViewModel",
            ["LoveNotesPage"] = "LoveNotesViewModel",
            ["AppearancePage"] = "AppearanceViewModel",
            ["ConnectionPage"] = "ConnectionViewModel",
            ["PrivacyDataPage"] = "PrivacyDataViewModel",
        };
        foreach (var (page, viewModel) in pageContracts)
        {
            var xaml = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml"));
            var code = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml.cs"));
            Assert.Contains($"x:Class=\"Dudu.App.Pages.{page}\"", xaml);
            Assert.Contains($"public sealed partial class {page}", code);
            Assert.Contains($"public {viewModel} ViewModel {{ get; }}", code);
            Assert.Contains("InitializeComponent();", code);
            Assert.Contains("DataContext = ViewModel;", code);
            Assert.Contains($"public sealed partial class {page}", stubs);
            if (page == "HomePage")
            {
                Assert.Contains("x:Name=\"StartupRecoveryPanel\"", xaml);
                Assert.Contains("x:Name=\"StartupRecoveryMessage\"", xaml);
                Assert.Contains("x:Name=\"RetryStartupButton\"", xaml);
                Assert.Contains("private StackPanel StartupRecoveryPanel", stubs);
                Assert.Contains("private TextBlock StartupRecoveryMessage", stubs);
                Assert.Contains("private Button RetryStartupButton", stubs);
            }
            else
            {
                Assert.DoesNotContain("x:Name=\"", xaml, StringComparison.Ordinal);
            }
            foreach (Match control in Regex.Matches(
                xaml,
                @"<(Button|CheckBox|ComboBox|Slider|TextBox|NumberBox|ListView|ItemsControl)\b[^>]*>",
                RegexOptions.Singleline))
            {
                Assert.Contains("AutomationProperties.AutomationId=\"", control.Value);
            }
        }

        foreach (var automationId in new[]
        {
            "HomePet", "RemindersSave", "TasksSave", "FocusStart", "LoveNotesSave",
            "AppearanceSave", "ConnectionCreateCode", "PrivacyBackup",
            "AppearanceOutfit", "AppearanceSeasonalMode",
        })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", allPages);
        }

        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanPersistOutfit, Mode=OneWay}\"", allPages);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanConfigureSeasonalMode, Mode=OneWay}\"", allPages);
        Assert.Contains("Outfit choices apply for this session only.", allPages);
        Assert.Contains("Automatic seasonal mode is unavailable", allPages);
        var automationIds = Regex.Matches(
                allPages,
                "AutomationProperties\\.AutomationId=\\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(automationIds);
        Assert.Equal(automationIds.Length, automationIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var page in pageContracts.Keys)
        {
            var xaml = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml"));
            Assert.Contains("Background=\"{ThemeResource WindowSurfaceBrush}\"", xaml);
            Assert.Contains("TextSecondaryBrush", xaml);
            Assert.Contains("ErrorBrush", xaml);
        }
        var overlay = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Overlay", "ActionBubbleLayout.cs"));
        Assert.Contains("Breathe with me", overlay);
        Assert.DoesNotContain("LinearGradientBrush", allPages, StringComparison.Ordinal);
        Assert.DoesNotContain("AcrylicBrush", allPages, StringComparison.Ordinal);
        Assert.DoesNotContain("Color=\"#", allPages, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", allPages, StringComparison.Ordinal);
        Assert.DoesNotContain("—", allPages, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_pages_keep_live_feature_bindings_and_exact_labels()
    {
        var root = FindRepositoryRoot();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HomePage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml")),
            ["RemindersPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "RemindersPage.xaml")),
            ["TasksFocusPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "TasksFocusPage.xaml")),
            ["LoveNotesPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "LoveNotesPage.xaml")),
            ["AppearancePage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "AppearancePage.xaml")),
            ["ConnectionPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "ConnectionPage.xaml")),
            ["PrivacyDataPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "PrivacyDataPage.xaml")),
        };
        var expectedBindings = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["HomePage.xaml"] = ["ViewModel.PetCommand", "ViewModel.CreateCountdownCommand", "ViewModel.RecordCheckInCommand"],
            ["RemindersPage.xaml"] = ["ViewModel.SaveCommand", "ViewModel.CompleteCommand", "ViewModel.SaveReminderPreferencesCommand"],
            ["TasksFocusPage.xaml"] = ["ViewModel.SaveTaskCommand", "ViewModel.StartFocusCommand", "ViewModel.EndFocusCommand"],
            ["LoveNotesPage.xaml"] = ["ViewModel.SaveLocalNoteCommand", "ViewModel.SaveOpenedNoteCommand"],
            ["AppearancePage.xaml"] = ["ViewModel.SaveCommand", "ViewModel.SavePlacementCommand", "ViewModel.SaveShortcutCommand"],
            ["ConnectionPage.xaml"] = ["ViewModel.CreateCodeCommand", "ViewModel.RevokeSessionsCommand", "ViewModel.DeleteRemoteDeviceCommand"],
            ["PrivacyDataPage.xaml"] = ["ViewModel.BackupCommand", "ViewModel.RestoreCommand", "ViewModel.DeleteLocalDataCommand", "ViewModel.DeleteRemoteDataCommand"],
        };
        var expectedLabels = new[]
        {
            "Home", "Reminders", "Tasks and Focus", "Love Notes", "Appearance", "Connection", "Privacy and Data",
            "Dudu's status", "Your reminders", "Active tasks", "Task details", "Focus", "Local note jar", "Incoming notes",
            "Look and motion", "Pet options", "Shortcut", "Pairing status", "Paired sessions", "Stored on this PC", "Your data",
        };

        foreach (var (name, page) in pages)
        {
            foreach (var binding in expectedBindings[name]) Assert.Contains(binding, page);
            foreach (Match control in Regex.Matches(page, "<(Button|CheckBox|ComboBox|ListView|NumberBox|Slider|TextBox|ItemsControl)\\b"))
            {
                var start = control.Index;
                var end = page.IndexOf('>', start);
                Assert.True(end > start, $"{name} contains an unterminated control tag.");
                Assert.Contains("AutomationProperties.AutomationId=", page[start..end], StringComparison.Ordinal);
            }
        }

        var allPages = string.Join("\n", pages.Values);
        foreach (var label in expectedLabels) Assert.Contains($"Text=\"{label}\"", allPages);
    }

    [Fact]
    public void Overlay_renderer_uses_controller_snapshot_and_keeps_surface_closed_by_default()
    {
        var root = FindRepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Overlay", "OverlayActionSurfaceController.cs"));
        var renderer = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Animation", "OverlaySurfaceRenderer.cs"));
        var presenter = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Animation", "SkiaFrameComposer.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Overlay", "OverlayWindowHost.cs"));

        Assert.Contains("Kind { get; private set; } = OverlayActionSurfaceKind.Closed", controller);
        Assert.Contains("CreateRenderSnapshot", controller);
        Assert.Contains("OverlaySurfaceRenderer.Draw", presenter);
        Assert.Contains("Breathe with me", controller + renderer);
        Assert.Contains("Reduced motion", renderer);
        Assert.Contains("WS_EX_NOACTIVATE", host);
        Assert.Contains("SW_SHOWNOACTIVATE", host);
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
