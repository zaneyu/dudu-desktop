using Xunit;
using System.Text.RegularExpressions;

namespace Dudu.App.Tests.Ui;

public sealed class XamlContractTests
{
    [Fact]
    public void App_constructor_initializes_merged_application_resources_before_startup()
    {
        var root = FindRepositoryRoot();
        // Finding 9 (test bug): normalized before the embedded-newline
        // search below -- a CRLF Windows checkout would otherwise leave a
        // literal "\r\n" where "\n    }" expects "\n", making IndexOf
        // silently fail to find the constructor's end.
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs")).Replace("\r\n", "\n");
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
        // Slider.StepFrequency is a real WinUI 3 property and defaults to 1: left
        // unset, the pet-size slider (0.5 to 2) snapped to 50%/150%/200% only, so the
        // recommended 100% could not be picked. The old DoesNotContain here pinned
        // that bug in place.
        Assert.Contains("StepFrequency=\"0.05\"", onboarding);
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
            ["HomePage"] = ["HomeNextReminder", "HomeActiveFocus", "HomePetState", "HomePetAnimation", "HomeActionStatus", "HomeNextCountdown", "CountdownTargetBox", "CountdownTargetValidation", "HomeSaveCountdownButton", "HomeCheckInSummary", "HomeCheckInHistory", "StartupToggle", "StartupRecoveryPanel", "StartupRecoveryMessage", "RetryStartupButton"],
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
        // Seasonal outfits (anniversary/birthday/winter) aren't shipped yet, so the
        // copy must say so honestly instead of claiming automatic mode does something
        // it can't currently do.
        Assert.Contains("aren't included in this version yet", allPages);
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

    [Fact]
    public void Every_command_parameter_x_bind_declares_an_explicit_mode()
    {
        // x:Bind defaults to OneTime. A CommandParameter left at OneTime is captured once,
        // at initial layout, when the bound selection is still null -- so the command always
        // receives null even after the user picks something. Every CommandParameter x:Bind
        // must say Mode=OneWay or Mode=TwoWay explicitly so it tracks the live selection.
        // {Binding} already defaults to OneWay, so it only fails here if it explicitly opts
        // back into OneTime. The pattern tolerates single quotes, whitespace around '=', and
        // one level of nested markup-extension braces (e.g. a converter parameter).
        var root = FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Dudu.App");
        var xamlFiles = Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        var commandParameterPattern = new Regex(
            "CommandParameter\\s*=\\s*([\"'])\\{(x:Bind|Binding)(?:[^{}]|\\{[^{}]*\\})*\\}\\1",
            RegexOptions.Singleline);
        var modePattern = new Regex(@"Mode\s*=\s*(OneWay|TwoWay|OneTime)\b");

        foreach (var file in xamlFiles)
        {
            var xaml = File.ReadAllText(file);
            foreach (Match binding in commandParameterPattern.Matches(xaml))
            {
                var extensionKind = binding.Groups[2].Value;
                var modeMatch = modePattern.Match(binding.Value);
                if (extensionKind == "x:Bind")
                {
                    Assert.True(
                        modeMatch.Success && modeMatch.Groups[1].Value is "OneWay" or "TwoWay",
                        $"{Path.GetFileName(file)} has a CommandParameter x:Bind with no explicit Mode=OneWay/TwoWay: {binding.Value}");
                }
                else
                {
                    Assert.False(
                        modeMatch.Success && modeMatch.Groups[1].Value == "OneTime",
                        $"{Path.GetFileName(file)} has a CommandParameter {{Binding}} pinned to Mode=OneTime, so it never tracks the live selection: {binding.Value}");
                }
            }
        }
    }

    [Fact]
    public void Startup_toggle_is_not_x_bound_and_is_initialized_before_the_view_model_refresh()
    {
        // Opus review regression: StartupToggle.IsChecked used to be
        // {x:Bind Startup.Current.LaunchAtSignIn, Mode=OneTime}. x:Bind evaluates during
        // InitializeComponent(), before Page_Loaded ever runs and before
        // _suppressStartupToggle exists, so setting IsChecked there fired the
        // Checked/Unchecked handler unsuppressed and performed a real OS
        // startup-registration write on every Home page load. The checkbox state must
        // instead come only from RefreshStartupRecovery(), which sets it under the
        // suppression flag, and that must run before (not after) the view-model refresh
        // so the checkbox reflects the real state immediately.
        var root = FindRepositoryRoot();
        var homeXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));
        var checkboxMatch = Regex.Match(homeXaml, "<CheckBox x:Name=\"StartupToggle\"[^>]*/>");
        Assert.True(checkboxMatch.Success, "StartupToggle CheckBox not found in HomePage.xaml.");
        Assert.DoesNotContain("IsChecked", checkboxMatch.Value, StringComparison.Ordinal);

        // Finding 9 (test bug): normalized before the embedded-newline
        // search below, for the same CRLF-checkout reason as appCode above.
        var homeCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml.cs")).Replace("\r\n", "\n");
        var loadedStart = homeCode.IndexOf("private async void Page_Loaded(", StringComparison.Ordinal);
        var loadedEnd = homeCode.IndexOf("\n    }", loadedStart, StringComparison.Ordinal);
        Assert.True(loadedStart >= 0);
        Assert.True(loadedEnd > loadedStart);
        var loadedBody = homeCode[loadedStart..loadedEnd];

        var refreshRecoveryIndex = loadedBody.IndexOf("RefreshStartupRecovery();", StringComparison.Ordinal);
        var refreshAsyncIndex = loadedBody.IndexOf("await ViewModel.RefreshAsync();", StringComparison.Ordinal);
        Assert.True(refreshRecoveryIndex >= 0, "Page_Loaded no longer calls RefreshStartupRecovery().");
        Assert.True(refreshAsyncIndex >= 0, "Page_Loaded no longer calls ViewModel.RefreshAsync().");
        Assert.True(
            refreshRecoveryIndex < refreshAsyncIndex,
            "RefreshStartupRecovery() must run before ViewModel.RefreshAsync() in Page_Loaded.");
    }

    [Fact]
    public void RefreshStartupRecovery_guards_its_own_body_so_its_async_void_callers_cannot_crash()
    {
        // Audit regression: RefreshStartupRecovery() ran unguarded inside three
        // async void handlers (Page_Loaded, RetryStartupButton_Click,
        // StartupToggle_Changed) with none of them wrapping the call in a
        // try/catch of their own -- an unhandled throw there is a process
        // crash. The method now guards its own body instead, so the call
        // order test above stays satisfied without touching those handlers.
        var root = FindRepositoryRoot();
        // Finding 9 (test bug): normalized before the embedded-newline
        // search below, for the same CRLF-checkout reason as appCode above.
        var homeCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml.cs")).Replace("\r\n", "\n");
        var methodStart = homeCode.IndexOf("private void RefreshStartupRecovery()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "HomePage.xaml.cs no longer declares RefreshStartupRecovery().");
        var methodEnd = homeCode.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var methodBody = homeCode[methodStart..methodEnd];

        Assert.Contains("try", methodBody, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", methodBody, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceError", methodBody, StringComparison.Ordinal);
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
