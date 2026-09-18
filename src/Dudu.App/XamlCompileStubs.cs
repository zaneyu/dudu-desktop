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
                ("reminders", "reminders", "NavReminders"),
                ("tasks and focus", "tasks", "NavTasksFocus"),
                ("love notes", "notes", "NavLoveNotes"),
                ("appearance", "appearance", "NavAppearance"),
                ("connection", "connection", "NavConnection"),
                ("privacy and data", "privacy", "NavPrivacy"),
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
        private TextBlock HomeNextReminder = null!;
        private TextBlock HomeActiveFocus = null!;
        private TextBlock HomePetState = null!;
        private TextBlock HomePetAnimation = null!;
        private TextBlock HomeActionStatus = null!;
        private TextBox CountdownTargetBox = null!;
        private TextBlock CountdownTargetValidation = null!;
        private Button HomeSaveCountdownButton = null!;
        private TextBlock HomeCheckInSummary = null!;
        private ItemsControl HomeCheckInHistory = null!;
        private StackPanel StartupRecoveryPanel = null!;
        private TextBlock StartupRecoveryMessage = null!;
        private Button RetryStartupButton = null!;

        private void InitializeComponent()
        {
            HomeNextReminder = new TextBlock();
            HomeActiveFocus = new TextBlock();
            HomePetState = new TextBlock();
            HomePetAnimation = new TextBlock();
            HomeActionStatus = new TextBlock();
            CountdownTargetBox = new TextBox();
            CountdownTargetValidation = new TextBlock();
            HomeSaveCountdownButton = new Button();
            HomeCheckInSummary = new TextBlock();
            HomeCheckInHistory = new ItemsControl();
            StartupRecoveryPanel = new StackPanel { Visibility = Visibility.Collapsed };
            StartupRecoveryMessage = new TextBlock();
            RetryStartupButton = new Button();
        }
    }

    public sealed partial class RemindersPage
    {
        private ListView ReminderList = null!;
        private ComboBox ScheduleBox = null!;
        private TextBox LocalTimeBox = null!;
        private TextBlock RemindersLocalTimeValidation = null!;
        private CheckBox SundayBox = null!;
        private CheckBox MondayBox = null!;
        private CheckBox TuesdayBox = null!;
        private CheckBox WednesdayBox = null!;
        private CheckBox ThursdayBox = null!;
        private CheckBox FridayBox = null!;
        private CheckBox SaturdayBox = null!;
        private NumberBox IntervalBox = null!;
        private ComboBox QuietHoursBox = null!;
        private Button SaveReminderButton = null!;

        private void InitializeComponent()
        {
            ReminderList = new ListView();
            ScheduleBox = new ComboBox();
            LocalTimeBox = new TextBox();
            RemindersLocalTimeValidation = new TextBlock();
            SundayBox = new CheckBox();
            MondayBox = new CheckBox();
            TuesdayBox = new CheckBox();
            WednesdayBox = new CheckBox();
            ThursdayBox = new CheckBox();
            FridayBox = new CheckBox();
            SaturdayBox = new CheckBox();
            IntervalBox = new NumberBox();
            QuietHoursBox = new ComboBox();
            SaveReminderButton = new Button();
        }
    }

    public sealed partial class TasksFocusPage
    {
        private TextBox TaskDueBox = null!;
        private TextBlock TaskDueValidation = null!;
        private Button SaveTaskButton = null!;
        private TextBlock FocusCurrent = null!;

        private void InitializeComponent()
        {
            TaskDueBox = new TextBox();
            TaskDueValidation = new TextBlock();
            SaveTaskButton = new Button();
            FocusCurrent = new TextBlock();
        }
    }

    public sealed partial class LoveNotesPage
    {
        private TextBlock LoveNotesDailyLimit = null!;
        private TextBlock LoveNotesPendingCount = null!;

        private void InitializeComponent()
        {
            LoveNotesDailyLimit = new TextBlock();
            LoveNotesPendingCount = new TextBlock();
        }
    }

    public sealed partial class AppearancePage
    {
        private ComboBox ThemeBox = null!;

        private void InitializeComponent()
        {
            ThemeBox = new ComboBox();
        }
    }

    public sealed partial class ConnectionPage
    {
        private TextBlock ConnectionAvailability = null!;
        private TextBlock ConnectionPairingCode = null!;
        private TextBlock ConnectionCodeExpiry = null!;
        private TextBlock ConnectionSessionCount = null!;

        private void InitializeComponent()
        {
            ConnectionAvailability = new TextBlock();
            ConnectionPairingCode = new TextBlock();
            ConnectionCodeExpiry = new TextBlock();
            ConnectionSessionCount = new TextBlock();
        }
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
