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

        // The pet-size slider left onboarding with the placement step; Settings
        // keeps its own (see the Settings slider-step test).
        Assert.DoesNotContain("<Slider", onboarding);
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
            "HomePage", "LoveNotesPage", "SettingsPage",
        };
        var pageFiles = pageNames
            .Select(page => Path.Combine(pagesDirectory, $"{page}.xaml"))
            .ToArray();

        Assert.Equal(3, pageFiles.Length);
        var allPages = string.Join("\n", pageFiles.Select(File.ReadAllText));
        var stubs = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "XamlCompileStubs.cs"));
        var pageContracts = new Dictionary<string, string>
        {
            ["HomePage"] = "HomeViewModel",
            ["LoveNotesPage"] = "LoveNotesViewModel",
            ["SettingsPage"] = "SettingsViewModel",
        };
        var expectedNamedElements = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["HomePage"] = ["HomeDuduImage", "HomeActionStatus"],
            ["LoveNotesPage"] = ["LoveNotesPendingCount"],
            ["SettingsPage"] = ["ThemeBox", "StartupToggle", "StartupRecoveryPanel", "StartupRecoveryMessage", "RetryStartupButton", "ConnectionAvailability", "ConnectionPairingCode", "ConnectionCodeExpiry", "ConnectionSessionCount"],
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
            "OverlayActionPet", "LoveNotesOpenedList",
            "AppearanceSave", "ConnectionCreateCode",
        })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", allPages);
        }

        // Outfits and seasonal dates are gone: animation always uses the base outfit.
        foreach (var removed in new[]
        {
            "AppearanceOutfit", "AppearanceSeasonalMode", "AppearanceSeasonalAvailability",
            "AppearanceAnniversary", "AppearanceBirthday", "AppearanceSaveSeasonal",
            "ViewModel.SelectedOutfit", "ViewModel.AnniversaryDate", "ViewModel.BirthdayDate",
            "ViewModel.OutfitAvailabilityMessage", "ViewModel.ApplyOutfitCommand", "CalendarDatePicker",
        })
        {
            Assert.DoesNotContain(removed, allPages, StringComparison.Ordinal);
        }
        var loveNotes = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "LoveNotesPage.xaml"));
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedRemoteEnvelope, Mode=TwoWay}\"", loveNotes);
        Assert.Contains("AutomationProperties.AutomationId=\"LoveNotesRevealSelected\"", loveNotes);
        Assert.DoesNotContain("RemoteNoteList_SelectionChanged", loveNotes);
        // Revealing a partner note keeps it in the opened list; the local note jar (editor,
        // "let dudu choose", daily limit, a separate "save opened note" step) is gone.
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedOpenedNote, Mode=TwoWay}\"", loveNotes);
        Assert.Contains("AutomationProperties.AutomationId=\"LoveNotesOpened\"", loveNotes);
        foreach (var removed in new[] { "LoveNotesDraft", "LoveNotesSave", "LoveNotesNew", "LoveNotesChoose", "LoveNotesSaveOpened", "LoveNotesLocalList", "LoveNotesDailyLimit", "jar" })
        {
            Assert.DoesNotContain(removed, loveNotes, StringComparison.Ordinal);
        }
        // LOW: the partner connection section's destructive-confirmation flow (F3/H3's
        // forget-pairing path among them) keeps its automation coverage on the Settings page.
        var connection = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionForgetPairing\"", connection);
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionConfirm\"", connection);
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionCancel\"", connection);
        Assert.DoesNotContain("seasonal", allPages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outfit", allPages, StringComparison.OrdinalIgnoreCase);
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
            ["LoveNotesPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "LoveNotesPage.xaml")),
            ["SettingsPage.xaml"] = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml")),
        };
        var expectedBindings = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["HomePage.xaml"] = ["ViewModel.GreetingText", "ViewModel.PartnerTimeText", "ViewModel.PartnerClockSpokenText", "ViewModel.PetCommand", "ViewModel.StatusMessage", "ViewModel.ErrorMessage"],
            ["LoveNotesPage.xaml"] = ["ViewModel.PendingRemoteNotes", "ViewModel.RevealRemoteNoteCommand", "ViewModel.OpenedNotes", "ViewModel.SelectedOpenedNote", "ViewModel.SelectedOpenedNoteText", "ViewModel.RequestDeleteOpenedNoteCommand", "ViewModel.DeleteOpenedNoteCommand", "ViewModel.CancelDeleteOpenedNoteCommand"],
            ["SettingsPage.xaml"] = ["ViewModel.SaveCommand", "ViewModel.SavePlacementCommand", "ViewModel.Connection.CreateCodeCommand", "ViewModel.Connection.RequestRevokeSessionsCommand", "ViewModel.Connection.RequestDeleteRemoteDeviceCommand", "ViewModel.Connection.RequestForgetPairingCommand", "ViewModel.Connection.ConfirmCommand", "ViewModel.Connection.CancelConfirmationCommand", "ViewModel.RequestDeleteMyDataCommand", "ViewModel.ConfirmDeleteMyDataCommand", "ViewModel.CancelDeleteMyDataCommand", "ViewModel.WipeThisPcOnlyCommand", "ViewModel.IsDeleteConfirmVisible", "ViewModel.IsWipeThisPcOnlyVisible"],
        };
        var expectedLabels = new[]
        {
            "home", "love notes", "settings",
            "time in the UK", "time with dudu", "incoming notes", "opened notes",
            "look and motion", "when windows starts", "partner connection", "pairing status", "paired sessions", "your data",
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
    public void Settings_shell_navigates_to_exactly_home_love_notes_and_settings()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs"));
        var stubs = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "XamlCompileStubs.cs"));

        var items = Regex.Matches(
                shell,
                "<NavigationViewItem Content=\"([^\"]+)\" Tag=\"([^\"]+)\" AutomationProperties\\.AutomationId=\"([^\"]+)\">")
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value))
            .ToArray();
        Assert.Equal(
            [("home", "home", "NavHome"), ("love notes", "notes", "NavLoveNotes"), ("settings", "settings", "NavSettings")],
            items);
        // Every item keeps an icon: LeftCompact shows only icons until the pane opens.
        Assert.Equal(items.Length, Regex.Matches(shell, "<NavigationViewItem\\.Icon>").Count);

        foreach (var (content, tag, automationId) in items)
        {
            Assert.Contains($"(\"{content}\", \"{tag}\", \"{automationId}\"),", stubs);
            Assert.Contains($"\"{tag}\" => _", code);
        }

        foreach (var removed in new[] { "reminders", "tasks", "appearance", "connection", "privacy" })
        {
            Assert.DoesNotContain($"Tag=\"{removed}\"", shell);
            Assert.DoesNotContain($"\"{removed}\" => _", code);
        }

        foreach (var removedPage in new[] { "RemindersPage", "TasksFocusPage", "AppearancePage", "ConnectionPage", "PrivacyDataPage" })
        {
            Assert.False(File.Exists(Path.Combine(root, "src", "Dudu.App", "Pages", $"{removedPage}.xaml")));
            Assert.DoesNotContain(removedPage, code);
            Assert.DoesNotContain($"partial class {removedPage}", stubs);
        }
    }

    [Fact]
    public void Every_cute_overlay_action_has_a_keyboard_accessible_home_counterpart()
    {
        // Home is the keyboard/UIA surface for every cute action: pet, drink,
        // eat together, tiny hug and breathe with me.
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));

        Assert.Equal(5, Dudu.App.Overlay.OverlayCommandRouter.AccessiblePrimaryActions.Count);
        foreach (var action in Dudu.App.Overlay.OverlayCommandRouter.AccessiblePrimaryActions)
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{action.AutomationId}\"", home);
            Assert.False(string.IsNullOrWhiteSpace(action.SettingsDestination));
        }

        // Tiny hug and breathe run through the overlay-action handler; the
        // comfort-panel handler (whose enum no longer has TinyHug) is gone.
        Assert.Contains("Tag=\"TinyHug\" Click=\"OverlayAction_Click\"", home);
        Assert.Contains("Tag=\"BreatheWithMe\" Click=\"OverlayAction_Click\"", home);
        Assert.DoesNotContain("ComfortAction_Click", home);
    }

    [Fact]
    public void Home_shows_the_breathing_panel_with_a_stop_button()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml.cs"));

        Assert.Contains("Visibility=\"{x:Bind Breathing.IsVisible, Mode=OneWay}\"", home);
        Assert.Contains("Text=\"{x:Bind Breathing.PhaseText, Mode=OneWay}\"", home);
        Assert.Contains("Text=\"{x:Bind Breathing.Instruction, Mode=OneWay}\"", home);
        Assert.Contains("Click=\"BreathingStop_Click\"", home);
        Assert.Contains("AutomationProperties.AutomationId=\"HomeBreathingStop\"", home);
        Assert.Contains("public BreathingPanelPresenter Breathing { get; }", code);
        Assert.Contains("private async void BreathingStop_Click(", code);
        // Stopping is the normal early end, not an error.
        Assert.Contains("catch (OperationCanceledException) when (action == OverlayAction.BreatheWithMe)", code);
        // Leaving Home stops only a running exercise.
        Assert.Contains("Breathing.StopIfBreathingAsync()", code);
        // The presenter exists before InitializeComponent, which x:Bind reads.
        Assert.True(
            code.IndexOf("Breathing = new BreathingPanelPresenter(", StringComparison.Ordinal)
                < code.IndexOf("InitializeComponent();", StringComparison.Ordinal));
    }

    [Fact]
    public void Home_is_cute_only()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml.cs"));

        Assert.Contains("x:Name=\"HomeDuduImage\"", home);
        Assert.Contains("AutomationProperties.AutomationId=\"HomeGreeting\"", home);
        Assert.Contains("AutomationProperties.AutomationId=\"HomePartnerClock\"", home);
        foreach (var banned in new[] { "Reminder", "Focus", "Countdown", "CheckIn", "Mood", "Pause", "Resume", "LoveNote", "ComfortMe", "FiveMinuteBreak", "ComfortActionClose", "Startup" })
        {
            Assert.DoesNotContain(banned, home, StringComparison.Ordinal);
            Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
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
        // startup-registration write on every page load. The checkbox (moved from Home to
        // Settings) state must
        // instead come only from RefreshStartupRecovery(), which sets it under the
        // suppression flag, and that must run before (not after) the view-model refresh
        // so the checkbox reflects the real state immediately.
        var root = FindRepositoryRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml"));
        var checkboxMatch = Regex.Match(settingsXaml, "<CheckBox x:Name=\"StartupToggle\"[^>]*/>");
        Assert.True(checkboxMatch.Success, "StartupToggle CheckBox not found in SettingsPage.xaml.");
        var homeXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "HomePage.xaml"));
        Assert.DoesNotContain("StartupToggle", homeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked", checkboxMatch.Value, StringComparison.Ordinal);

        // Finding 9 (test bug): normalized before the embedded-newline
        // search below, for the same CRLF-checkout reason as appCode above.
        var settingsCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml.cs")).Replace("\r\n", "\n");
        var loadedStart = settingsCode.IndexOf("private async void Page_Loaded(", StringComparison.Ordinal);
        var loadedEnd = settingsCode.IndexOf("\n    }", loadedStart, StringComparison.Ordinal);
        Assert.True(loadedStart >= 0);
        Assert.True(loadedEnd > loadedStart);
        var loadedBody = settingsCode[loadedStart..loadedEnd];

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
        var settingsCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml.cs")).Replace("\r\n", "\n");
        var methodStart = settingsCode.IndexOf("private void RefreshStartupRecovery()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "SettingsPage.xaml.cs no longer declares RefreshStartupRecovery().");
        var methodEnd = settingsCode.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var methodBody = settingsCode[methodStart..methodEnd];

        Assert.Contains("try", methodBody, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", methodBody, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceError", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Appearance_layering_toggles_sit_with_the_button_that_saves_them()
    {
        // "keep dudu above other windows" and "hide dudu during fullscreen work"
        // are saved by "save appearance", so they must sit above that button
        // (they once sat under a separate "save seasonal look" that did not save
        // them, and changes made there were lost).
        var root = FindRepositoryRoot();
        var appearance = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml"));
        var save = appearance.IndexOf("AutomationProperties.AutomationId=\"AppearanceSave\"", StringComparison.Ordinal);
        var alwaysOnTop = appearance.IndexOf("AutomationProperties.AutomationId=\"AppearanceAlwaysOnTop\"", StringComparison.Ordinal);
        var hideFullscreen = appearance.IndexOf("AutomationProperties.AutomationId=\"AppearanceHideFullscreen\"", StringComparison.Ordinal);

        Assert.True(save > 0);
        Assert.InRange(alwaysOnTop, 0, save);
        Assert.InRange(hideFullscreen, 0, save);
        // The hotkey only ever shows Dudu and opens Home; it never hides her.
        Assert.DoesNotContain("Header=\"show or hide dudu\"", appearance, StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_buttons_stay_a_plain_style_and_meet_text_contrast()
    {
        // Audit regression: white on the old coral was only ~4.0:1. PrimaryButtonStyle
        // must not be BasedOn a framework style: that StaticResource lookup from a merged
        // theme dictionary cannot be verified off Windows and a miss fails startup.
        var root = FindRepositoryRoot();
        var controls = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Controls.xaml"));
        var colors = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Colors.xaml"));

        Assert.Contains("<Style x:Key=\"PrimaryButtonStyle\" TargetType=\"Button\">", controls);
        var themes = Regex.Matches(
            colors,
            "<ResourceDictionary x:Key=\"(?<theme>\\w+)\">(?<body>.*?)</ResourceDictionary>",
            RegexOptions.Singleline)
            .ToDictionary(match => match.Groups["theme"].Value, match => match.Groups["body"].Value);
        foreach (var theme in new[] { "Default", "Light", "Dark" })
        {
            var body = themes[theme];
            var ratio = ContrastRatio(BrushColor(body, "CoralActionBrush"), BrushColor(body, "PrimaryButtonForegroundBrush"));
            Assert.True(ratio >= 4.5, $"{theme} CoralActionBrush: {ratio:F2}:1 is below 4.5:1");
        }
    }

    private static string BrushColor(string dictionary, string key)
    {
        var match = Regex.Match(dictionary, $"<SolidColorBrush x:Key=\"{key}\" Color=\"#(?<color>[0-9A-Fa-f]{{6,8}})\" />");
        Assert.True(match.Success, $"{key} is missing a literal color.");
        var color = match.Groups["color"].Value;
        return color.Length == 8 ? color[2..] : color;
    }

    private static double ContrastRatio(string first, string second)
    {
        static double Luminance(string hex)
        {
            static double Channel(string part)
            {
                var value = Convert.ToInt32(part, 16) / 255.0;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(hex[..2])) + (0.7152 * Channel(hex[2..4])) + (0.0722 * Channel(hex[4..6]));
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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
