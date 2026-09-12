#if DUDU_XAML_STUBS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Windows
{
    public sealed partial class SettingsWindow
    {
        private Grid AppTitleBar = null!;
        private NavigationView RootNavigation = null!;
        private Frame ContentFrame = null!;

        private void InitializeComponent()
        {
            AppTitleBar = new Grid();
            RootNavigation = new NavigationView();
            ContentFrame = new Frame();
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
                var navigationItem = new NavigationViewItem { Content = item.Item1, Tag = item.Item2 };
                AutomationProperties.SetAutomationId(navigationItem, item.Item3);
                RootNavigation.MenuItems.Add(navigationItem);
            }
        }
    }
}

namespace Dudu.App.Pages
{
    public sealed partial class HomePage
    {
        private StackPanel StartupRecoveryPanel = null!;
        private TextBlock StartupRecoveryMessage = null!;
        private Button RetryStartupButton = null!;

        private void InitializeComponent()
        {
            StartupRecoveryPanel = new StackPanel { Visibility = Visibility.Collapsed };
            StartupRecoveryMessage = new TextBlock();
            RetryStartupButton = new Button();
        }
    }

    public sealed partial class RemindersPage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class TasksFocusPage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class LoveNotesPage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class AppearancePage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class ConnectionPage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class PrivacyDataPage
    {
        private void InitializeComponent() { }
    }

    public sealed partial class OnboardingPage
    {
        private TextBlock OnboardingProgress = null!;
        private StackPanel RecipientStep = null!;
        private StackPanel AppearanceStep = null!;
        private StackPanel QuietHoursStep = null!;
        private StackPanel RemindersStep = null!;
        private StackPanel PlacementStep = null!;
        private StackPanel PairingStep = null!;
        private TextBox RecipientNameBox = null!;
        private Button RecommendedDefaultsButton = null!;
        private ComboBox ThemeBox = null!;
        private CheckBox ReducedMotionBox = null!;
        private CheckBox QuietHoursBox = null!;
        private TextBox QuietStartBox = null!;
        private TextBox QuietEndBox = null!;
        private CheckBox HydrationBox = null!;
        private CheckBox BreakBox = null!;
        private NumberBox NoteLimitBox = null!;
        private Button RecommendedPlacementButton = null!;
        private Slider PlacementScaleSlider = null!;
        private CheckBox HideFullscreenBox = null!;
        private CheckBox LaunchAtSignInBox = null!;
        private StackPanel StartupRecoveryPanel = null!;
        private TextBlock StartupRecoveryMessage = null!;
        private TextBlock PairingStatus = null!;
        private Button PairingCheckButton = null!;
        private Button SkipPairingButton = null!;
        private TextBlock OnboardingValidationMessage = null!;
        private Button BackButton = null!;
        private Button NextButton = null!;
        private Button CompleteButton = null!;

        private void InitializeComponent()
        {
            OnboardingProgress = new TextBlock();
            RecipientStep = new StackPanel();
            AppearanceStep = new StackPanel();
            QuietHoursStep = new StackPanel();
            RemindersStep = new StackPanel();
            PlacementStep = new StackPanel();
            PairingStep = new StackPanel();
            RecipientNameBox = new TextBox();
            RecommendedDefaultsButton = new Button();
            ThemeBox = new ComboBox();
            ReducedMotionBox = new CheckBox();
            QuietHoursBox = new CheckBox();
            QuietStartBox = new TextBox();
            QuietEndBox = new TextBox();
            HydrationBox = new CheckBox();
            BreakBox = new CheckBox();
            NoteLimitBox = new NumberBox { Value = 3 };
            RecommendedPlacementButton = new Button();
            PlacementScaleSlider = new Slider { Minimum = 0.5, Maximum = 2, Value = 1 };
            HideFullscreenBox = new CheckBox();
            LaunchAtSignInBox = new CheckBox();
            StartupRecoveryPanel = new StackPanel();
            StartupRecoveryMessage = new TextBlock();
            PairingStatus = new TextBlock();
            PairingCheckButton = new Button();
            SkipPairingButton = new Button();
            OnboardingValidationMessage = new TextBlock();
            BackButton = new Button();
            NextButton = new Button();
            CompleteButton = new Button();
        }
    }
}
#endif
