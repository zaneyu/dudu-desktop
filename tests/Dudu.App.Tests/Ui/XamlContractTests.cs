using Xunit;
using System.Text.RegularExpressions;

namespace Dudu.App.Tests.Ui;

public sealed class XamlContractTests
{
    [Fact]
    public void App_constructor_initializes_merged_application_resources_before_startup()
    {
        var root = FindRepositoryRoot();
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml"));
        var constructorStart = appCode.IndexOf("public App()", StringComparison.Ordinal);
        var constructorEnd = appCode.IndexOf("\n    }", constructorStart, StringComparison.Ordinal);

        Assert.True(constructorStart >= 0);
        Assert.True(constructorEnd > constructorStart);
        var constructor = appCode[constructorStart..constructorEnd];
        Assert.Contains("InitializeComponent();", constructor);
        Assert.Contains("Source=\"Themes/Colors.xaml\"", appXaml);
        Assert.Contains("Source=\"Themes/Controls.xaml\"", appXaml);
    }

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
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{ThemeResource PrimaryButtonForegroundBrush}\" />", controls);
        Assert.Contains("SystemColorHighlightTextColor", colors);
        Assert.Contains("private StackPanel StartupRecoveryPanel", stubs);
        Assert.Contains("private TextBlock StartupRecoveryMessage", stubs);
        foreach (Match tag in Regex.Matches(
            onboarding,
            "<[A-Za-z][^<>]*AutomationProperties\\.AutomationId=\"[^\"]+\"[^<>]*>",
            RegexOptions.Singleline))
        {
            Assert.Contains("AutomationProperties.Name=\"", tag.Value);
        }
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
        var expectedNamedElements = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["HomePage"] = ["HomeNextReminder", "HomeActiveFocus", "HomePetState", "HomePetAnimation", "HomeActionStatus", "CountdownTargetBox", "CountdownTargetValidation", "HomeSaveCountdownButton", "HomeCheckInSummary", "HomeCheckInHistory", "StartupRecoveryPanel", "StartupRecoveryMessage", "RetryStartupButton"],
            ["RemindersPage"] = ["ReminderList", "ScheduleBox", "LocalTimeBox", "RemindersLocalTimeValidation", "SundayBox", "MondayBox", "TuesdayBox", "WednesdayBox", "ThursdayBox", "FridayBox", "SaturdayBox", "IntervalBox", "QuietHoursBox", "SaveReminderButton"],
            ["TasksFocusPage"] = ["TaskDueBox", "TaskDueValidation", "SaveTaskButton", "FocusCurrent"],
            ["LoveNotesPage"] = ["LoveNotesDailyLimit", "LoveNotesPendingCount"],
            ["AppearancePage"] = ["ThemeBox"],
            ["ConnectionPage"] = ["ConnectionAvailability", "ConnectionPairingCode", "ConnectionCodeExpiry", "ConnectionSessionCount"],
            ["PrivacyDataPage"] = [],
        };
        var viewModelSources = pageContracts.ToDictionary(
            pair => pair.Key,
            pair => File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "ViewModels", $"{pair.Value}.cs")),
            StringComparer.Ordinal);
        var baseViewModelSource = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "ViewModels", "CompanionFeatureContext.cs"));
        foreach (var (page, viewModel) in pageContracts)
        {
            var xaml = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml"));
            var code = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml.cs"));
            Assert.Contains($"x:Class=\"Dudu.App.Pages.{page}\"", xaml);
            Assert.Contains($"public sealed partial class {page}", code);
            Assert.Contains($"public {viewModel} ViewModel {{ get; }}", code);
            Assert.Contains("InitializeComponent();", code);
            Assert.Contains("DataContext = ViewModel;", code);
            var namedElements = Regex.Matches(xaml, "x:Name=\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Assert.Equal(expectedNamedElements[page], namedElements);
            var stubBody = ExtractClassBody(stubs, page);
            var stubFields = Regex.Matches(stubBody, @"private\s+\w+\s+(\w+)\s*=\s*null!;")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Assert.Equal(namedElements, stubFields);
            foreach (var element in namedElements)
            {
                Assert.Contains($"{element} = new", stubBody);
            }
            var apiNames = ExtractPublicMemberNames(viewModelSources[page] + baseViewModelSource);
            foreach (Match binding in Regex.Matches(xaml, @"ViewModel\.([A-Za-z_]\w*)"))
            {
                Assert.Contains(binding.Groups[1].Value, apiNames);
            }
            foreach (Match control in Regex.Matches(
                xaml,
                @"<(Button|CheckBox|ComboBox|Slider|TextBox|NumberBox|ListView|ItemsControl)(?=[\s/>])[^>]*>",
                RegexOptions.Singleline))
            {
                Assert.Contains("AutomationProperties.AutomationId=\"", control.Value);
            }
        }

        foreach (var automationId in new[]
        {
            "OverlayActionPet", "RemindersSave", "TasksSave", "FocusStart", "LoveNotesSave",
            "AppearanceSave", "ConnectionCreateCode", "PrivacyBackup",
            "AppearanceOutfit", "AppearanceSeasonalMode", "AppearanceSaveSeasonal",
        })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", allPages);
        }

        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedOutfit, Mode=TwoWay}\"", allPages);
        Assert.Contains("Date=\"{x:Bind ViewModel.AnniversaryDate, Mode=TwoWay}\"", allPages);
        Assert.Contains("Date=\"{x:Bind ViewModel.BirthdayDate, Mode=TwoWay}\"", allPages);
        Assert.Contains("ViewModel.OutfitAvailabilityMessage", allPages);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.RecentCheckIns, Mode=OneWay}\"", File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml")));
        var loveNotes = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "LoveNotesPage.xaml"));
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedRemoteEnvelope, Mode=TwoWay}\"", loveNotes);
        Assert.Contains("AutomationProperties.AutomationId=\"LoveNotesRevealSelected\"", loveNotes);
        Assert.DoesNotContain("RemoteNoteList_SelectionChanged", loveNotes);
        var privacy = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "PrivacyDataPage.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"PrivacyConfirmationMessage\"", privacy);
        Assert.Contains("AutomationProperties.AutomationId=\"PrivacyConfirm\"", privacy);
        // LOW: the Connection page's own destructive-confirmation flow (F3/H3's forget-pairing
        // path among them) deserves the same automation coverage Privacy's already has.
        var connection = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "ConnectionPage.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionForgetPairing\"", connection);
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionConfirm\"", connection);
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionCancel\"", connection);
        Assert.Contains("automatic mode checks these dates", allPages);
        var automationIds = Regex.Matches(
                allPages,
                "AutomationProperties\\.AutomationId=\\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(automationIds);
        Assert.Equal(automationIds.Length, automationIds.Distinct(StringComparer.Ordinal).Count());
        foreach (Match tag in Regex.Matches(
            allPages,
            "<[A-Za-z][^<>]*AutomationProperties\\.AutomationId=\"[^\"]+\"[^<>]*>",
            RegexOptions.Singleline))
        {
            Assert.Contains("AutomationProperties.Name=\"", tag.Value);
        }
        foreach (var page in pageContracts.Keys)
        {
            var xaml = File.ReadAllText(Path.Combine(pagesDirectory, $"{page}.xaml"));
            Assert.Contains("Background=\"{ThemeResource WindowSurfaceBrush}\"", xaml);
            Assert.Contains("TextSecondaryBrush", xaml);
            Assert.Contains("ErrorBrush", xaml);
        }
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
            ["HomePage.xaml"] = ["ViewModel.PauseDescription", "ViewModel.PetCommand", "ViewModel.PauseForOneHourCommand", "ViewModel.ResumeCommand", "ViewModel.CountdownTitle", "ViewModel.Countdowns", "ViewModel.CreateCountdownCommand", "ViewModel.SelectedCountdown", "ViewModel.DeleteCountdownCommand", "ViewModel.CheckInNote", "ViewModel.RecordCheckInCommand", "ViewModel.StatusMessage", "ViewModel.ErrorMessage"],
            ["RemindersPage.xaml"] = ["ViewModel.Reminders", "ViewModel.SaveCommand", "ViewModel.CompleteCommand", "ViewModel.SnoozeCommand", "ViewModel.SaveReminderPreferencesCommand"],
            ["TasksFocusPage.xaml"] = ["ViewModel.ActiveTasks", "ViewModel.CompletedTasks", "ViewModel.FocusHistory", "ViewModel.SaveTaskCommand", "ViewModel.StartFocusCommand", "ViewModel.PauseFocusCommand", "ViewModel.ResumeFocusCommand", "ViewModel.ExtendFocusCommand", "ViewModel.EndFocusCommand"],
            ["LoveNotesPage.xaml"] = ["ViewModel.LocalNotes", "ViewModel.PendingRemoteNotes", "ViewModel.SaveLocalNoteCommand", "ViewModel.DeleteLocalNoteCommand", "ViewModel.ShowLocalNoteCommand", "ViewModel.SaveOpenedNoteCommand"],
            ["AppearancePage.xaml"] = ["ViewModel.SaveCommand", "ViewModel.SavePlacementCommand", "ViewModel.SaveShortcutCommand"],
            ["ConnectionPage.xaml"] = ["ViewModel.CreateCodeCommand", "ViewModel.RequestRevokeSessionsCommand", "ViewModel.RequestDeleteRemoteDeviceCommand", "ViewModel.RequestForgetPairingCommand", "ViewModel.ConfirmCommand", "ViewModel.CancelConfirmationCommand"],
            ["PrivacyDataPage.xaml"] = ["ViewModel.BackupCommand", "ViewModel.RequestRestoreCommand", "ViewModel.RequestDeleteLocalDataCommand", "ViewModel.RequestDeleteRemoteDataCommand", "ViewModel.ConfirmCommand", "ViewModel.CancelConfirmationCommand"],
        };
        var expectedLabels = new[]
        {
            "home", "reminders", "tasks and focus", "love notes", "appearance", "connection", "privacy and data",
            "dudu status", "your reminders", "active tasks", "task details", "focus", "local note jar", "incoming notes",
            "look and motion", "pet options", "shortcut", "pairing status", "paired sessions", "stored on this pc", "your data",
            "weekdays", "completed tasks", "focus history", "dudu actions", "comfort actions",
        };

        foreach (var (name, page) in pages)
        {
            foreach (var binding in expectedBindings[name]) Assert.Contains(binding, page);
            foreach (Match control in Regex.Matches(page, "<(Button|CheckBox|ComboBox|ListView|NumberBox|Slider|TextBox|ItemsControl)(?=[\\s/>])"))
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
    public void Settings_shell_keeps_dudu_present_across_every_destination()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs"));
        var stubs = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "XamlCompileStubs.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"DuduCompanionPanel\"", shell);
        Assert.Contains("x:Name=\"DuduFrameImage\"", shell);
        Assert.Contains("x:Name=\"OnboardingFrame\"", shell);
        Assert.Contains("Click=\"DuduPetButton_Click\"", shell);
        Assert.Contains("Click=\"DuduDrinkButton_Click\"", shell);
        Assert.Contains("Click=\"DuduComfortButton_Click\"", shell);
        Assert.Contains("AssetManifestLoader.LoadAsync", code);
        Assert.Contains("DuduCompanionStatus.Text", code);
        Assert.Contains("private Image DuduFrameImage", stubs);
        Assert.Contains("private Frame OnboardingFrame", stubs);
        Assert.Contains("private Button DuduComfortButton", stubs);
    }

    [Fact]
    public void Every_painted_overlay_action_has_a_keyboard_accessible_settings_counterpart()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));

        foreach (var action in Dudu.App.Overlay.OverlayCommandRouter.AccessiblePrimaryActions)
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{action.AutomationId}\"", home);
            Assert.False(string.IsNullOrWhiteSpace(action.SettingsDestination));
        }

        foreach (var action in Dudu.App.Overlay.OverlayCommandRouter.AccessibleComfortActions)
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{action.AutomationId}\"", home);
            Assert.False(string.IsNullOrWhiteSpace(action.SettingsDestination));
        }
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

    private static HashSet<string> ExtractPublicMemberNames(string source)
    {
        return Regex.Matches(
                source,
                @"\bpublic\s+(?:[\w<>,.?\[\]]+\s+)+(?<name>[A-Za-z_]\w*)\s*(?:\{|=>|\()")
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ExtractClassBody(string source, string className)
    {
        var marker = $"public sealed partial class {className}";
        var classStart = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(classStart >= 0, $"The XAML stub for {className} is missing.");
        var bodyStart = source.IndexOf('{', classStart);
        Assert.True(bodyStart >= 0, $"The XAML stub for {className} has no body.");
        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[(bodyStart + 1)..index];
        }

        throw new InvalidOperationException($"The XAML stub for {className} is not balanced.");
    }
}
