#if DUDU_XAML_STUBS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App
{
    public sealed partial class App
    {
        private void InitializeComponent() { }
    }
}

namespace Dudu.App.Windows
{
    public sealed partial class SettingsWindow
    {
        private Grid RootGrid = null!;
        private Grid AppTitleBar = null!;
        private NavigationView RootNavigation = null!;
        private Frame ContentFrame = null!;
        private Frame OnboardingFrame = null!;
        private Image DuduFrameImage = null!;
        private TextBlock DuduCompanionTitle = null!;
        private TextBlock DuduCompanionMessage = null!;
        private TextBlock DuduCompanionState = null!;
        private TextBlock DuduCompanionStatus = null!;
        private Button DuduPetButton = null!;
        private Button DuduDrinkButton = null!;
        private Button DuduComfortButton = null!;

        private void InitializeComponent()
        {
            RootGrid = new Grid();
            AppTitleBar = new Grid();
            RootNavigation = new NavigationView();
            ContentFrame = new Frame();
            OnboardingFrame = new Frame();
            DuduFrameImage = new Image();
            DuduCompanionTitle = new TextBlock();
            DuduCompanionMessage = new TextBlock();
            DuduCompanionState = new TextBlock();
            DuduCompanionStatus = new TextBlock();
            DuduPetButton = new Button();
            DuduDrinkButton = new Button();
            DuduComfortButton = new Button();
            foreach (var item in new[]
            {
                ("home", "home", "NavHome"),
                ("love notes", "notes", "NavLoveNotes"),
                ("settings", "settings", "NavSettings"),
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
        private Image HomeDuduImage = null!;
        private TextBlock HomeActionStatus = null!;

        private void InitializeComponent()
        {
            HomeDuduImage = new Image();
            HomeActionStatus = new TextBlock();
        }
    }

    public sealed partial class LoveNotesPage
    {
        private TextBlock LoveNotesPendingCount = null!;

        private void InitializeComponent()
        {
            LoveNotesPendingCount = new TextBlock();
        }
    }

    public sealed partial class SettingsPage
    {
        private ComboBox ThemeBox = null!;
        private CheckBox StartupToggle = null!;
        private StackPanel StartupRecoveryPanel = null!;
        private TextBlock StartupRecoveryMessage = null!;
        private Button RetryStartupButton = null!;
        private TextBlock ConnectionAvailability = null!;
        private TextBlock ConnectionPairingCode = null!;
        private TextBlock ConnectionCodeExpiry = null!;
        private TextBlock ConnectionSessionCount = null!;

        private void InitializeComponent()
        {
            ThemeBox = new ComboBox();
            StartupToggle = new CheckBox();
            StartupRecoveryPanel = new StackPanel { Visibility = Visibility.Collapsed };
            StartupRecoveryMessage = new TextBlock();
            RetryStartupButton = new Button();
            ConnectionAvailability = new TextBlock();
            ConnectionPairingCode = new TextBlock();
            ConnectionCodeExpiry = new TextBlock();
            ConnectionSessionCount = new TextBlock();
        }
    }

    public sealed partial class OnboardingPage
    {
        private TextBlock OnboardingProgress = null!;
        private StackPanel RecipientStep = null!;
        private StackPanel AppearanceStep = null!;
        private StackPanel RemindersStep = null!;
        private StackPanel PlacementStep = null!;
        private StackPanel PairingStep = null!;
        private TextBox RecipientNameBox = null!;
        private Button RecommendedDefaultsButton = null!;
        private ComboBox ThemeBox = null!;
        private CheckBox ReducedMotionBox = null!;
        private CheckBox HydrationBox = null!;
        private CheckBox BreakBox = null!;
        private NumberBox NoteLimitBox = null!;
        private Button RecommendedPlacementButton = null!;
        private Slider PlacementScaleSlider = null!;
        private TextBlock PlacementScaleLabel = null!;
        private CheckBox HideFullscreenBox = null!;
        private CheckBox LaunchAtSignInBox = null!;
        private StackPanel StartupRecoveryPanel = null!;
        private TextBlock StartupRecoveryMessage = null!;
        private TextBlock PairingStatus = null!;
        private Button PairingCheckButton = null!;
        private Button SkipPairingButton = null!;
        private TextBlock OnboardingStatusMessage = null!;
        private TextBlock OnboardingValidationMessage = null!;
        private Button BackButton = null!;
        private Button NextButton = null!;
        private Button CompleteButton = null!;

        private void InitializeComponent()
        {
            OnboardingProgress = new TextBlock();
            RecipientStep = new StackPanel();
            AppearanceStep = new StackPanel();
            RemindersStep = new StackPanel();
            PlacementStep = new StackPanel();
            PairingStep = new StackPanel();
            RecipientNameBox = new TextBox();
            RecommendedDefaultsButton = new Button();
            ThemeBox = new ComboBox();
            ReducedMotionBox = new CheckBox();
            HydrationBox = new CheckBox();
            BreakBox = new CheckBox();
            NoteLimitBox = new NumberBox { Value = 3 };
            RecommendedPlacementButton = new Button();
            PlacementScaleSlider = new Slider { Minimum = 0.5, Maximum = 2, Value = 1, StepFrequency = 0.05 };
            PlacementScaleLabel = new TextBlock();
            HideFullscreenBox = new CheckBox();
            LaunchAtSignInBox = new CheckBox();
            StartupRecoveryPanel = new StackPanel();
            StartupRecoveryMessage = new TextBlock();
            PairingStatus = new TextBlock();
            PairingCheckButton = new Button();
            SkipPairingButton = new Button();
            OnboardingStatusMessage = new TextBlock();
            OnboardingValidationMessage = new TextBlock();
            BackButton = new Button();
            NextButton = new Button();
            CompleteButton = new Button();
        }
    }
}
#endif
