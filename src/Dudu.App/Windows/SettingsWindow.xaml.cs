using Dudu.App.Pages;
using Dudu.App.Hosting;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Windows;

public sealed partial class SettingsWindow : UserControl
{
    private readonly CompanionSettingsContext _context;
    private readonly SettingsShellViewModel _shell;
    private readonly OnboardingViewModel _onboarding;
    private readonly HomePage _homePage;
    private readonly RemindersPage _remindersPage;
    private readonly TasksFocusPage _tasksFocusPage;
    private readonly LoveNotesPage _loveNotesPage;
    private readonly AppearancePage _appearancePage;
    private readonly ConnectionPage _connectionPage;
    private readonly PrivacyDataPage _privacyDataPage;
    private bool _initialized;

    public SettingsWindow(CompanionSettingsContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _shell = new SettingsShellViewModel();
        _onboarding = new OnboardingViewModel(
            context.StartupSettings.PreferenceMutations,
            context.Profiles,
            context.PetPlacements,
            context.UnitOfWork,
            context.StartupSettings,
            context.Pairing,
            initialPlacement: context.PlacementSnapshot.Placement,
            runtimeApplier: async (preferences, placement, cancellationToken) =>
            {
                await context.ApplyRuntimeAsync(preferences, placement, cancellationToken);
            },
            placementCapture: context.CapturePlacementAsync,
            placementPreviewer: context.ApplyPlacementAsync);
        var features = context.Features
            ?? throw new InvalidOperationException("Feature settings are not available.");
        _homePage = new HomePage(new HomeViewModel(features), context.StartupSettings);
        _remindersPage = new RemindersPage(new RemindersViewModel(features));
        _tasksFocusPage = new TasksFocusPage(new TasksFocusViewModel(features));
        _loveNotesPage = new LoveNotesPage(new LoveNotesViewModel(features));
        _appearancePage = new AppearancePage(new AppearanceViewModel(features));
        _connectionPage = new ConnectionPage(new ConnectionViewModel(features));
        _privacyDataPage = new PrivacyDataPage(new PrivacyDataViewModel(features));
        InitializeComponent();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        Loaded += OnLoaded;
    }

    public FrameworkElement TitleBarElement => AppTitleBar;

    /// <summary>Production overlay/tray route for feature destinations.</summary>
    public void NavigateTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ShowDestination(destination);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await _onboarding.LoadAsync();
            ApplyRequestedTheme();
            if (_onboarding.IsComplete)
            {
                ShowDestination("home");
            }
            else
            {
                RootNavigation.Visibility = Visibility.Collapsed;
                ContentFrame.Content = new OnboardingPage(
                    _onboarding,
                    OnboardingCompleted,
                    _context.SetUserVisibleAsync);
            }
        }
        catch (Exception exception)
        {
            ContentFrame.Content = new TextBlock
            {
                Text = "Dudu could not load settings. Try opening settings again.",
                Margin = new Thickness(32),
                TextWrapping = TextWrapping.Wrap,
            };
            global::System.Diagnostics.Trace.TraceError("Dudu settings load failed: {0}", exception);
        }
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowDestination(tag);
        }
    }

    private void ShowDestination(string tag)
    {
        if (!_shell.Navigate(tag)) return;
        RootNavigation.SelectedItem = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));

        ContentFrame.Content = tag switch
        {
            "home" => _homePage,
            "reminders" => _remindersPage,
            "tasks" => _tasksFocusPage,
            "notes" => _loveNotesPage,
            "appearance" => _appearancePage,
            "connection" => _connectionPage,
            "privacy" => _privacyDataPage,
            _ => _homePage,
        };
    }

    private void OnboardingCompleted()
    {
        ApplyRequestedTheme();
        RootNavigation.Visibility = Visibility.Visible;
        ShowDestination("home");
    }

    private void ApplyRequestedTheme()
    {
        RequestedTheme = _onboarding.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
