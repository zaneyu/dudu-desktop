using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Pages;
using Dudu.App.System;
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
        // No initial SelectedItem here: ShowDestination selects the real
        // destination once the pages exist. Selecting Home up front raised a
        // SelectionChanged that could land after App's NavigateTo(...) from a toast
        // or the overlay and overwrite the pending destination with "home".
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

        var previous = ContentFrame.Content;
        ShowDestination(destination);
        if (!ReferenceEquals(previous, ContentFrame.Content))
        {
            // An external navigation (a Home action button, the overlay, a toast)
            // swapped the page out from under keyboard focus; park focus on the
            // destination's nav item instead of losing it.
            FocusSelectedNavigationItem();
        }
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
                    // There is no in-window retry; say how to actually try again.
                    Text = "aiyo couldnt load settings. close this window and open it again",
                    Margin = new Thickness(32),
                    TextWrapping = TextWrapping.Wrap,
                };
                AutomationProperties.SetAutomationId(error, "SettingsLoadError");
                AutomationProperties.SetName(error, error.Text);
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
            SetCompanionStatus("dudu art unavailable this run");
            global::System.Diagnostics.Trace.TraceError("Dudu settings companion visual failed: {0}", exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _duduTimer.Stop();

        // Defense in depth for the same leak TasksFocusPage.Page_Unloaded
        // already guards against: App builds a fresh SettingsWindow (-> new
        // page -> new view model) on every open, so a subscription left on
        // the singleton FocusService.SessionExpired that never gets
        // detached leaks forever. Page_Unloaded may not fire at all when
        // the window is closed outright rather than navigated away from;
        // this Unloaded on the window's own content is a second chance to
        // detach in that case. Idempotent (DetachFocusExpiry no-ops if
        // already detached), and safe if feature pages were never created
        // (e.g. closed mid-onboarding).
        _tasksFocusPage?.ViewModel.DetachFocusExpiry();
        // The shared reminder actions outlive this window; stop refreshing a
        // page nobody can see (Unloaded does not always run on window close).
        _remindersPage?.DetachLiveUpdates();
    }

    /// <summary>Called by App when an already-open settings window is shown again
    /// (tray, pet, toast). Cached pages only refresh on Loaded, which does not fire for
    /// the page already on screen, so it kept showing whatever it loaded on its last
    /// visit -- a reminder that has since fired, a note that has since arrived. Re-runs
    /// the same view-model refresh its Loaded handler does. Appearance is left alone
    /// (its refresh would overwrite unsaved edits, and nothing else changes it) and
    /// Privacy has no stored data to reload.</summary>
    public async void RefreshCurrentPage()
    {
        if (!_featurePagesInitialized) return;
        Func<Task>? refresh = ContentFrame.Content switch
        {
            HomePage { IsLoaded: true } page => () => page.ViewModel.RefreshAsync(),
            RemindersPage { IsLoaded: true } page => () => page.ViewModel.RefreshAsync(),
            TasksFocusPage { IsLoaded: true } page => () => page.ViewModel.RefreshAsync(),
            LoveNotesPage { IsLoaded: true } page => () => page.ViewModel.RefreshAsync(),
            ConnectionPage { IsLoaded: true } page => () => page.ViewModel.RefreshAsync(),
            _ => null,
        };
        if (refresh is null) return;

        try
        {
            await refresh();
        }
        catch (Exception exception)
        {
            // async void: never let a refresh failure escape to the dispatcher.
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu settings reopen refresh failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    /// <summary>Called by App from the hosting Window's Closed event, the one close signal
    /// WinUI 3 raises reliably (Unloaded is not guaranteed for a closed window's content).</summary>
    public void OnHostWindowClosed()
    {
        _tasksFocusPage?.ViewModel.DetachFocusExpiry();
        // The page countdown timers run on the app's UI thread, which outlives this
        // window; left running they would tick (and keep the pages alive) forever.
        _tasksFocusPage?.StopFocusCountdown();
        _homePage?.StopFocusCountdown();
    }

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
        }
        catch (Exception exception)
        {
            // A throw on a DispatcherTimer tick is unhandled and repeats every
            // 150 ms. Freeze on the last good frame instead of crashing the app.
            _duduTimer.Stop();
            SetCompanionStatus("dudu art unavailable this run");
            global::System.Diagnostics.Trace.TraceError("Dudu settings companion tick failed: {0}", exception);
        }
    }

    private void DuduTimer_TickCore()
    {
        if (_duduPack is null) return;

        var preferences = _context.Features?.CurrentPreferences ?? Preferences.Default;
        if (preferences.ReducedMotion)
        {
            _duduFrameIndex = 0;
        }
        else if (_duduAnimation is { Frames.Count: > 1 })
        {
            if (_duduAnimation.Loop == "loop")
            {
                _duduFrameIndex = (_duduFrameIndex + 1) % _duduAnimation.Frames.Count;
            }
            else if (_duduFrameIndex < _duduAnimation.Frames.Count - 1)
            {
                _duduFrameIndex++;
            }
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
        }

        _duduAnimation = animation;
        _duduFrameIndex = Math.Clamp(_duduFrameIndex, 0, animation.Frames.Count - 1);
        var frame = animation.Frames[_duduFrameIndex];
        var fullPath = Path.GetFullPath(Path.Combine(_duduPack.RootDirectory, frame.File));
        if (!_duduImages.TryGetValue(fullPath, out var image))
        {
            image = new BitmapImage(new Uri(fullPath, UriKind.Absolute));
            _duduImages[fullPath] = image;
        }

        DuduFrameImage.Source = image;
        DuduCompanionTitle.Text = DuduTitle(presentation);
        DuduCompanionMessage.Text = DuduMessage(presentation);
        // Plain language shared with Home's pet status; the raw enum and asset key
        // ("remotenote · note-hold") meant nothing to the person using the app.
        DuduCompanionState.Text = HomeViewModel.DescribePetState(presentation.State);
        AutomationProperties.SetName(DuduFrameImage, $"dudu {presentation.AnimationKey} pose");
        _duduTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(frame.DurationMs, 80, 1000));
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
        await RunDuduActionAsync(features => features.PetAsync(CancellationToken.None), "dudu loves the pats le");

    private async void DuduDrinkButton_Click(object sender, RoutedEventArgs args) =>
        await RunDuduActionAsync(features => features.DrinkAsync(CancellationToken.None), "dudu says drink some water");

    private async void DuduComfortButton_Click(object sender, RoutedEventArgs args) =>
        await RunDuduOneShotAsync(
            new PetEvent.ComfortRequested(),
            "comfort",
            "dudu gives u a tiny hug");

    private Task RunDuduOneShotAsync(PetEvent petEvent, string dismissalId, string status) =>
        RunDuduActionAsync(
            features => features.PresentOneShotPetAsync(petEvent, dismissalId, CancellationToken.None),
            status);

    private async Task RunDuduActionAsync(Func<CompanionFeatureContext, Task> action, string status)
    {
        var features = _context.Features;
        if (features is null)
        {
            SetCompanionStatus("dudu is still getting ready");
            return;
        }

        SetCompanionStatus(status);
        try
        {
            await action(features);
        }
        catch (Exception exception)
        {
            SetCompanionStatus("aiyo dudu couldnt do that yet");
            global::System.Diagnostics.Trace.TraceError("Dudu settings action failed: {0}", exception);
        }
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            // User-driven: focus is already on the nav item, so skip NavigateTo's
            // focus hand-off. ShowDestination records it as pending before load.
            ShowDestination(tag);
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
        _remindersPage = new RemindersPage(new RemindersViewModel(features, _context.ReminderActions));
        _tasksFocusPage = new TasksFocusPage(new TasksFocusViewModel(features)
        {
            // FocusService.SessionExpired (a naturally-expired session, see
            // TasksFocusViewModel.OnFocusSessionExpired) is raised from the
            // background reminder tick thread, not guaranteed to be the UI
            // thread. Without this, MutateAsync runs that reload's mutations
            // inline on whichever thread raised the event. AwaitableUiDispatcher
            // runs inline when already on the UI thread (HasThreadAccess), so
            // this only adds real marshalling for the off-thread case.
            UiDispatcher = new AwaitableUiDispatcher(
                () => DispatcherQueue.HasThreadAccess,
                callback => DispatcherQueue.TryEnqueue(() => callback())).InvokeAsync,
        });
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
        // Setup is already committed when this runs. A failure building the
        // feature pages must not surface as "cant finish setup" on the onboarding
        // page (which would invite re-running a completed setup), and must not
        // escape into the page's async void click handler.
        try
        {
            ApplyRequestedTheme();
            EnsureFeaturePages();
            OnboardingFrame.Visibility = Visibility.Collapsed;
            OnboardingFrame.Content = null;
            RootNavigation.Visibility = Visibility.Visible;
            ShowDestination(_pendingDestination ?? "home");
            FocusSelectedNavigationItem();

            // The onboarding page -- the only place these were shown -- is gone
            // now, so a live-apply or startup-registration failure recorded during
            // completion would otherwise vanish silently.
            var followUp = _onboarding.RuntimeApplyError ?? _onboarding.StartupRegistrationError;
            if (followUp is not null)
            {
                SetCompanionStatus(followUp);
            }
        }
        catch (Exception exception)
        {
            RootNavigation.Visibility = Visibility.Visible;
            OnboardingFrame.Visibility = Visibility.Collapsed;
            SetCompanionStatus("setup saved. close this window and open it again to finish loading");
            global::System.Diagnostics.Trace.TraceError("Dudu settings failed to open after onboarding: {0}", exception);
        }
    }

    private void FocusSelectedNavigationItem()
    {
        if (RootNavigation.SelectedItem is not Control item) return;
        var queue = DispatcherQueue;
        if (queue is null || !queue.TryEnqueue(() => item.Focus(FocusState.Programmatic)))
        {
            item.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>The status line is a polite live region; its UIA name must be the
    /// message itself (a static "dudu action status" name hid the text from screen
    /// readers) and a LiveRegionChanged event is what makes Narrator announce it.
    /// Neutral colour: it carries errors as well as confirmations.</summary>
    private void SetCompanionStatus(string text)
    {
        DuduCompanionStatus.Text = text;
        AutomationProperties.SetName(DuduCompanionStatus, text);
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(DuduCompanionStatus)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(DuduCompanionStatus);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceInformation(
                "Dudu companion status announcement failed: {0}",
                exception.GetType().Name);
        }
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
