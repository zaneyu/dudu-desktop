using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Pages;
using Dudu.App.ViewModels;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Dudu.App.Windows;

public sealed partial class SettingsWindow : UserControl
{
    private readonly CompanionSettingsContext _context;
    private readonly SettingsShellViewModel _shell;
    private readonly OnboardingViewModel _onboarding;
    private HomePage? _homePage;
    private RemindersPage? _remindersPage;
    private TasksFocusPage? _tasksFocusPage;
    private LoveNotesPage? _loveNotesPage;
    private AppearancePage? _appearancePage;
    private ConnectionPage? _connectionPage;
    private PrivacyDataPage? _privacyDataPage;
    private string? _pendingDestination;
    private bool _featurePagesInitialized;
    private bool _initialized;
    private readonly DispatcherTimer _duduTimer;
    private readonly Dictionary<string, BitmapImage> _duduImages = new(StringComparer.OrdinalIgnoreCase);
    private AssetPack? _duduPack;
    private AssetAnimation? _duduAnimation;
    private string? _duduAnimationSignature;
    private int _duduFrameIndex;
    private DateTime _duduDeadlineUtc = DateTime.UtcNow;
    private int _duduConsecutiveErrors;

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
            runtimeApplier: (preferences, placement, cancellationToken) =>
                context.ApplyRuntimeAsync(preferences, placement, cancellationToken),
            placementCapture: context.CapturePlacementAsync,
            placementPreviewer: context.ApplyPlacementAsync);
        InitializeComponent();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        _duduTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _duduTimer.Tick += DuduTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public FrameworkElement TitleBarElement => AppTitleBar;

    /// <summary>Production route for overlay and tray actions. Before
    /// onboarding completes, retain the request rather than constructing pages
    /// against preferences the user has not accepted yet.</summary>
    public void NavigateTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!_featurePagesInitialized)
        {
            _pendingDestination = destination;
            return;
        }

        ShowDestination(destination);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_initialized)
        {
            _initialized = true;
            try
            {
                await _onboarding.LoadAsync();
                ApplyRequestedTheme();
                if (_onboarding.IsComplete)
                {
                    EnsureFeaturePages();
                    ShowDestination(_pendingDestination ?? "home");
                }
                else
                {
                    RootNavigation.Visibility = Visibility.Collapsed;
                    OnboardingFrame.Visibility = Visibility.Visible;
                    OnboardingFrame.Content = new OnboardingPage(
                        _onboarding,
                        OnboardingCompleted,
                        _context.SetUserVisibleAsync);
                }
            }
            catch (Exception exception)
            {
                var error = new TextBlock
                {
                    Text = "aiyo couldnt load settings try again",
                    Margin = new Thickness(32),
                    TextWrapping = TextWrapping.Wrap,
                };
                AutomationProperties.SetAutomationId(error, "SettingsLoadError");
                AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
                ContentFrame.Content = error;
                global::System.Diagnostics.Trace.TraceError("Dudu settings load failed: {0}", exception);
            }
        }

        try
        {
            await EnsureDuduPackAsync();
            _duduTimer.Start();
            RefreshDuduVisual();
        }
        catch (Exception exception)
        {
            DuduCompanionStatus.Text = "dudu art unavailable this run";
            global::System.Diagnostics.Trace.TraceError("Dudu settings companion visual failed: {0}", exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _duduTimer.Stop();

    private async Task EnsureDuduPackAsync()
    {
        if (_duduPack is not null) return;

        var packsRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Packs");
        var privateManifest = Path.Combine(packsRoot, "private-dudu", "manifest.json");
        var fallbackManifest = Path.Combine(packsRoot, "fallback", "manifest.json");
        try
        {
            _duduPack = await AssetManifestLoader.LoadAsync(privateManifest, CancellationToken.None);
        }
        catch (AssetManifestException)
        {
            _duduPack = await AssetManifestLoader.LoadAsync(fallbackManifest, CancellationToken.None);
        }
    }

    private void DuduTimer_Tick(object? sender, object args)
    {
        try
        {
            DuduTimer_TickCore();
            _duduConsecutiveErrors = 0;
        }
        catch (Exception exception)
        {
            // A throw on a DispatcherTimer tick repeats every interval. Keep
            // the timer alive on transient single failures (freeze on the
            // last good frame) and only park it after sustained errors so
            // one bad frame never kills the preview for the session.
            _duduConsecutiveErrors++;
            DuduCompanionStatus.Text = "dudu art unavailable this run";
            global::System.Diagnostics.Trace.TraceError("Dudu settings companion tick failed: {0}", exception);
            if (_duduConsecutiveErrors >= 5)
            {
                _duduTimer.Stop();
            }
        }
    }

    private void DuduTimer_TickCore()
    {
        if (_duduPack is null) return;

        var preferences = _context.Features?.CurrentPreferences ?? Preferences.Default;
        if (preferences.ReducedMotion)
        {
            _duduFrameIndex = 0;
            RefreshDuduVisual();
            return;
        }

        // Deadline-based pacing from frame.DurationMs: advancing by elapsed
        // deadline instead of index+1 keeps the preview from drifting when a
        // tick fires late, and drops (rather than queues) missed frames.
        // The BitmapImage cache in RefreshDuduVisual means ticks never
        // reload art on the UI thread; only the first sighting decodes.
        var now = DateTime.UtcNow;
        if (_duduAnimation is not { Frames.Count: > 1 })
        {
            RefreshDuduVisual();
            return;
        }

        var animation = _duduAnimation;
        if (now < _duduDeadlineUtc)
        {
            RefreshDuduVisual();
            return;
        }

        var guard = 0;
        while (now >= _duduDeadlineUtc && guard++ < animation.Frames.Count + 2)
        {
            if (animation.Loop == "loop")
            {
                _duduFrameIndex = (_duduFrameIndex + 1) % animation.Frames.Count;
            }
            else if (_duduFrameIndex < animation.Frames.Count - 1)
            {
                _duduFrameIndex++;
            }
            else
            {
                break;
            }

            var stepMs = Math.Clamp(animation.Frames[_duduFrameIndex].DurationMs, 80, 1000);
            _duduDeadlineUtc += TimeSpan.FromMilliseconds(stepMs);
        }

        if (now >= _duduDeadlineUtc)
        {
            // Hopelessly behind (suspended UI thread): drop the backlog and
            // re-anchor the deadline to now instead of spiralling.
            var currentMs = Math.Clamp(animation.Frames[_duduFrameIndex].DurationMs, 80, 1000);
            _duduDeadlineUtc = now + TimeSpan.FromMilliseconds(currentMs);
        }

        RefreshDuduVisual();
    }

    private void RefreshDuduVisual()
    {
        if (_duduPack is null) return;

        var presentation = _context.Features?.Pet.Current
            ?? new PetPresentation(PetState.Idle, "idle", null, null, false);
        var preferences = _context.Features?.CurrentPreferences ?? Preferences.Default;
        var outfit = preferences.AutomaticSeasonalMode
            ? null
            : preferences.OutfitKey ?? "base";
        var dates = new SeasonalDates(preferences.Anniversary, preferences.Birthday);
        var animation = _duduPack.ResolveAnimation(
            presentation.AnimationKey,
            DateOnly.FromDateTime(DateTime.Now),
            dates,
            outfit);
        var signature = $"{presentation.AnimationKey}|{outfit}|{animation.Frames.Count}|{animation.Loop}";
        if (!string.Equals(_duduAnimationSignature, signature, StringComparison.Ordinal))
        {
            _duduAnimationSignature = signature;
            _duduAnimation = animation;
            _duduFrameIndex = 0;
            _duduDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Clamp(animation.Frames[0].DurationMs, 80, 1000));
        }

        _duduAnimation = animation;
        _duduFrameIndex = Math.Clamp(_duduFrameIndex, 0, animation.Frames.Count - 1);
        var frame = animation.Frames[_duduFrameIndex];
        var fullPath = Path.GetFullPath(Path.Combine(_duduPack.RootDirectory, frame.File));
        if (!_duduImages.TryGetValue(fullPath, out var image))
        {
            // Cached per path: ticks after the first sighting reuse the
            // BitmapImage instead of reloading on the UI thread per tick.
            image = new BitmapImage(new Uri(fullPath, UriKind.Absolute));
            _duduImages[fullPath] = image;
        }

        DuduFrameImage.Source = image;
        DuduCompanionTitle.Text = DuduTitle(presentation);
        DuduCompanionMessage.Text = DuduMessage(presentation);
        DuduCompanionState.Text = $"{presentation.State.ToString().ToLowerInvariant()} · {presentation.AnimationKey}";
        AutomationProperties.SetName(DuduFrameImage, $"dudu {presentation.AnimationKey} pose");
        // Align the next tick to the deadline (compensates overshoot) rather
        // than a fixed frame duration, so drift never accumulates.
        var remaining = _duduDeadlineUtc - DateTime.UtcNow;
        if (remaining < TimeSpan.FromMilliseconds(16)) remaining = TimeSpan.FromMilliseconds(16);
        var frameMs = Math.Clamp(frame.DurationMs, 80, 1000);
        if (remaining > TimeSpan.FromMilliseconds(frameMs)) remaining = TimeSpan.FromMilliseconds(frameMs);
        _duduTimer.Interval = remaining;
    }

    private static string DuduTitle(PetPresentation presentation) => presentation.State switch
    {
        PetState.Comfort => "dudu has u",
        PetState.RemoteNote => "dudu brought a note",
        PetState.Reminder => "dudu remembers",
        PetState.Focus => "dudu is staying close",
        PetState.FocusTransition => "dudu says well done",
        PetState.WelcomeBack => "dudu missed u",
        PetState.Ambient => "dudu is having a moment",
        _ => "dudu is here",
    };

    private static string DuduMessage(PetPresentation presentation) => presentation switch
    {
        { BubbleBody: { Length: > 0 } body } => body,
        { BubbleTitle: { Length: > 0 } title } => title,
        { State: PetState.Comfort } => "tiny hug or slow breathing, ur choice",
        { State: PetState.Focus } => "quiet company while u do ur thing",
        { State: PetState.FocusTransition } => "nice work lihai, take a breath",
        { State: PetState.WelcomeBack } => "welcome back le",
        { State: PetState.Ambient } => "just checking in, no need to reply",
        _ => "ready to keep u company",
    };

    private async void DuduPetButton_Click(object sender, RoutedEventArgs args) =>
        await RunDuduOneShotAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            "dudu says hi le");

    private async void DuduDrinkButton_Click(object sender, RoutedEventArgs args) =>
        await RunDuduOneShotAsync(
            new PetEvent.AmbientRequested("drink"),
            "drink",
            "dudu says drink some water");

    private async void DuduComfortButton_Click(object sender, RoutedEventArgs args) =>
        await RunDuduOneShotAsync(
            new PetEvent.ComfortRequested(),
            "comfort",
            "dudu gives u a tiny hug");

    private async Task RunDuduOneShotAsync(PetEvent petEvent, string dismissalId, string status)
    {
        var features = _context.Features;
        if (features is null)
        {
            DuduCompanionStatus.Text = "dudu is still getting ready";
            return;
        }

        DuduCompanionStatus.Text = status;
        try
        {
            await features.PresentOneShotPetAsync(petEvent, dismissalId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            DuduCompanionStatus.Text = "aiyo dudu couldnt do that yet";
            global::System.Diagnostics.Trace.TraceError("Dudu settings action failed: {0}", exception);
        }
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void EnsureFeaturePages()
    {
        if (_featurePagesInitialized) return;
        var features = _context.Features
            ?? throw new InvalidOperationException("Feature settings are not available.");

        _homePage = new HomePage(
            new HomeViewModel(features),
            _context.StartupSettings,
            _context.OverlayCommands);
        _remindersPage = new RemindersPage(new RemindersViewModel(features));
        _tasksFocusPage = new TasksFocusPage(new TasksFocusViewModel(features));
        _loveNotesPage = new LoveNotesPage(new LoveNotesViewModel(features));
        _appearancePage = new AppearancePage(new AppearanceViewModel(
            features,
            ApplyRequestedTheme,
            _context.AvailableOutfitKeys));
        _connectionPage = new ConnectionPage(new ConnectionViewModel(features));
        _privacyDataPage = new PrivacyDataPage(new PrivacyDataViewModel(features));
        _featurePagesInitialized = true;
    }

    private void ShowDestination(string tag)
    {
        if (!_featurePagesInitialized)
        {
            _pendingDestination = tag;
            return;
        }

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
        EnsureFeaturePages();
        OnboardingFrame.Visibility = Visibility.Collapsed;
        RootNavigation.Visibility = Visibility.Visible;
        ShowDestination(_pendingDestination ?? "home");
    }

    private void ApplyRequestedTheme()
    {
        ApplyRequestedTheme(_onboarding.Theme);
    }

    private void ApplyRequestedTheme(AppTheme theme)
    {
        RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
