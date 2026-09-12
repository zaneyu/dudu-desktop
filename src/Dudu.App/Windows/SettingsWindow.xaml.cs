using Dudu.App.Pages;
using Dudu.App.Hosting;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dudu.App.Windows;

public sealed partial class SettingsWindow : UserControl
{
    private readonly CompanionSettingsContext _context;
    private readonly SettingsShellViewModel _shell;
    private readonly OnboardingViewModel _onboarding;
    private bool _initialized;

    public SettingsWindow(CompanionSettingsContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _shell = new SettingsShellViewModel();
        _onboarding = new OnboardingViewModel(
            context.Preferences,
            context.Profiles,
            context.PetPlacements,
            context.UnitOfWork,
            context.StartupRegistration,
            context.Pairing,
            initialPlacement: context.InitialPlacement,
            runtimeApplier: async (preferences, placement, cancellationToken) =>
            {
                context.StartupSettings.Adopt(preferences);
                await context.ApplyRuntimeAsync(preferences, placement, cancellationToken);
            });
        InitializeComponent();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        Loaded += OnLoaded;
    }

    public FrameworkElement TitleBarElement => AppTitleBar;

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
                ContentFrame.Content = new OnboardingPage(_onboarding, OnboardingCompleted);
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
            "home" => CreateHomePage(),
            "appearance" => CreateAppearancePage(),
            "connection" => CreateStatusPage("Connection", "Pairing is optional. Dudu stays useful offline."),
            "privacy" => CreateStatusPage("Privacy and data", "Your local notes and settings stay on this device."),
            "reminders" => CreateStatusPage("Reminders", "Set gentle reminders when you are ready."),
            "tasks" => CreateStatusPage("Tasks and focus", "Keep one small next step in view."),
            "notes" => CreateStatusPage("Love notes", "Read or add a local note whenever it helps."),
            _ => CreateHomePage(),
        };
    }

    private FrameworkElement CreateHomePage() => new StackPanel
    {
        Spacing = 16,
        Padding = new Thickness(32),
        Children =
        {
            new TextBlock { Text = "Home", Style = (Style)Application.Current.Resources["PageTitleStyle"] },
            new TextBlock
            {
                Text = $"Dudu is ready for {(_onboarding.RecipientName.Length == 0 ? "you" : _onboarding.RecipientName)}.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
            },
            new TextBlock
            {
                Text = _onboarding.RuntimeApplyError ?? string.Empty,
                Visibility = string.IsNullOrWhiteSpace(_onboarding.RuntimeApplyError)
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                Foreground = (Brush)Application.Current.Resources["WarningBrush"],
                TextWrapping = TextWrapping.Wrap,
            },
            new Border
            {
                Background = (Brush)Application.Current.Resources["BlushSurfaceBrush"],
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = "Small, quiet support for the day ahead.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            CreateStartupToggle(),
        },
    };

    private FrameworkElement CreateStartupToggle()
    {
        var toggle = new CheckBox
        {
            Content = "Launch Dudu when I sign in",
            IsChecked = _context.StartupSettings.Current.LaunchAtSignIn,
        };
        AutomationProperties.SetAutomationId(toggle, "SettingsLaunchAtSignIn");
        toggle.Checked += StartupToggle_Changed;
        toggle.Unchecked += StartupToggle_Changed;
        return toggle;
    }

    private async void StartupToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox toggle || toggle.IsChecked is not bool enabled) return;
        try
        {
            await _context.StartupSettings.SetLaunchAtSignInAsync(enabled);
        }
        catch (Exception exception)
        {
            toggle.IsChecked = !enabled;
            global::System.Diagnostics.Trace.TraceError("Dudu startup setting failed: {0}", exception);
        }
    }

    private FrameworkElement CreateAppearancePage() => new StackPanel
    {
        Spacing = 16,
        Padding = new Thickness(32),
        Children =
        {
            new TextBlock { Text = "Appearance", Style = (Style)Application.Current.Resources["PageTitleStyle"] },
            new TextBlock { Text = "Appearance follows your onboarding choices for now." },
            new TextBlock { Text = $"Theme: {_onboarding.Theme}; reduced motion: {(_onboarding.ReducedMotion ? "on" : "off")}." },
        },
    };

    private static FrameworkElement CreateStatusPage(string title, string message) => new StackPanel
    {
        Spacing = 16,
        Padding = new Thickness(32),
        Children =
        {
            new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] },
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        },
    };

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
