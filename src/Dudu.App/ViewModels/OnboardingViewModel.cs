using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

public enum OnboardingStep
{
    Recipient = 0,
    Appearance = 1,
    QuietHours = 2,
    Reminders = 3,
    Placement = 4,
    Pairing = 5,
}

/// <summary>
/// Draft-only onboarding state. Nothing is written until CompleteAsync has
/// validated the whole draft and committed the profile, preferences, and
/// placement through one application unit of work.
/// </summary>
public sealed class OnboardingViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public const int StepCount = 6;
    public const string DefaultMonitorDeviceName = "PRIMARY";

    private readonly IPreferencesRepository _preferencesRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IPetPlacementRepository _petPlacementRepository;
    private readonly IAppUnitOfWork _unitOfWork;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly IPairingService _pairingService;
    private readonly string _monitorDeviceName;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private bool _disposed;
    private string _recipientName = string.Empty;
    private AppTheme _theme = AppTheme.System;
    private bool _reducedMotion;
    private bool _quietHoursEnabled = true;
    private TimeOnly _quietHoursStart = new(22, 0);
    private TimeOnly _quietHoursEnd = new(7, 0);
    private int _localNoteDailyLimit = 3;
    private bool _hydrationRemindersEnabled = true;
    private bool _breakRemindersEnabled = true;
    private bool _hidePetDuringFullscreen = true;
    private bool _launchAtSignIn = true;
    private double _placementX = 0.8;
    private double _placementY = 0.8;
    private double _placementScale = 1.0;
    private OnboardingStep _currentStep;
    private PairingAvailability _pairingAvailability = PairingAvailability.Offline;
    private bool _pairingSkipped = true;
    private bool _isCompleting;
    private bool _isComplete;
    private string? _validationMessage;

    public OnboardingViewModel(
        IPreferencesRepository preferencesRepository,
        IProfileRepository profileRepository,
        IPetPlacementRepository petPlacementRepository,
        IAppUnitOfWork unitOfWork,
        StartupRegistrationService startupRegistration,
        IPairingService pairingService,
        string? monitorDeviceName = null)
    {
        _preferencesRepository = preferencesRepository ?? throw new ArgumentNullException(nameof(preferencesRepository));
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _petPlacementRepository = petPlacementRepository ?? throw new ArgumentNullException(nameof(petPlacementRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _startupRegistration = startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _monitorDeviceName = string.IsNullOrWhiteSpace(monitorDeviceName)
            ? DefaultMonitorDeviceName
            : monitorDeviceName.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RecipientName { get => _recipientName; set => Set(ref _recipientName, value ?? string.Empty); }
    public AppTheme Theme { get => _theme; set => Set(ref _theme, value); }
    public bool ReducedMotion { get => _reducedMotion; set => Set(ref _reducedMotion, value); }
    public bool QuietHoursEnabled { get => _quietHoursEnabled; set => Set(ref _quietHoursEnabled, value); }
    public TimeOnly QuietHoursStart { get => _quietHoursStart; set => Set(ref _quietHoursStart, value); }
    public TimeOnly QuietHoursEnd { get => _quietHoursEnd; set => Set(ref _quietHoursEnd, value); }
    public int LocalNoteDailyLimit { get => _localNoteDailyLimit; set => Set(ref _localNoteDailyLimit, value); }
    public bool HydrationRemindersEnabled { get => _hydrationRemindersEnabled; set => Set(ref _hydrationRemindersEnabled, value); }
    public bool BreakRemindersEnabled { get => _breakRemindersEnabled; set => Set(ref _breakRemindersEnabled, value); }
    public bool HidePetDuringFullscreen { get => _hidePetDuringFullscreen; set => Set(ref _hidePetDuringFullscreen, value); }
    public bool LaunchAtSignIn { get => _launchAtSignIn; set => Set(ref _launchAtSignIn, value); }
    public double PlacementX { get => _placementX; set => Set(ref _placementX, value); }
    public double PlacementY { get => _placementY; set => Set(ref _placementY, value); }
    public double PlacementScale { get => _placementScale; set => Set(ref _placementScale, value); }
    public OnboardingStep CurrentStep { get => _currentStep; private set => Set(ref _currentStep, value); }
    public PairingAvailability PairingAvailability { get => _pairingAvailability; private set => Set(ref _pairingAvailability, value); }
    public bool PairingSkipped { get => _pairingSkipped; private set => Set(ref _pairingSkipped, value); }
    public bool IsCompleting { get => _isCompleting; private set => Set(ref _isCompleting, value); }
    public bool IsComplete { get => _isComplete; private set => Set(ref _isComplete, value); }
    public string? ValidationMessage { get => _validationMessage; private set => Set(ref _validationMessage, value); }
    public bool CanGoBack => CurrentStep > OnboardingStep.Recipient && !IsCompleting && !IsComplete;
    public string ProgressText => $"Step {(int)CurrentStep + 1} of {StepCount}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _navigationGate.WaitAsync(cancellationToken);
        try
        {
            var preferences = await _preferencesRepository.GetAsync(cancellationToken);
            if (preferences is not null)
            {
                Theme = preferences.Theme;
                ReducedMotion = preferences.ReducedMotion;
                QuietHoursEnabled = preferences.QuietHours.Enabled;
                QuietHoursStart = preferences.QuietHours.Start;
                QuietHoursEnd = preferences.QuietHours.End;
                LocalNoteDailyLimit = preferences.LocalNoteDailyLimit;
                LaunchAtSignIn = preferences.LaunchAtSignIn;
                HidePetDuringFullscreen = preferences.HidePetDuringFullscreen;
            }

            var profile = await _profileRepository.GetAsync(cancellationToken);
            if (profile is not null)
            {
                RecipientName = profile.RecipientName;
                IsComplete = profile.OnboardingComplete;
            }

            var placement = await _petPlacementRepository.GetAsync(_monitorDeviceName, cancellationToken);
            if (placement is not null)
            {
                PlacementX = placement.NormalizedX;
                PlacementY = placement.NormalizedY;
                PlacementScale = MonitorPlacementService.ClampScale(placement.Scale);
            }

            PairingAvailability = await _pairingService.GetStateAsync(cancellationToken);
            PairingSkipped = PairingAvailability == PairingAvailability.Offline;
            OnPropertyChanged(nameof(CanGoBack));
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public async Task AcceptRecommendedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _navigationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuietHoursEnabled = true;
            QuietHoursStart = new TimeOnly(22, 0);
            QuietHoursEnd = new TimeOnly(7, 0);
            LocalNoteDailyLimit = 3;
            HidePetDuringFullscreen = true;
            ReducedMotion = false;
            LaunchAtSignIn = true;
            HydrationRemindersEnabled = true;
            BreakRemindersEnabled = true;
            ValidationMessage = null;
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public void SkipPairing()
    {
        ThrowIfDisposed();
        PairingSkipped = true;
        ValidationMessage = null;
    }

    public async Task RefreshPairingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PairingAvailability = await _pairingService.GetStateAsync(cancellationToken);
        if (PairingAvailability == PairingAvailability.Offline)
        {
            SkipPairing();
        }
    }

    public async Task<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var complete = false;
        await _navigationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidationMessage = ValidateCurrentStep();
            if (ValidationMessage is not null)
            {
                return false;
            }

            if (CurrentStep == OnboardingStep.Pairing)
            {
                complete = true;
            }
            else
            {
                CurrentStep = (OnboardingStep)((int)CurrentStep + 1);
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(CanGoBack));
                return true;
            }
        }
        finally
        {
            _navigationGate.Release();
        }

        return complete && await CompleteAsync(cancellationToken);
    }

    public void Back()
    {
        ThrowIfDisposed();
        if (!_navigationGate.Wait(0))
        {
            return;
        }

        try
        {
            if (CurrentStep > OnboardingStep.Recipient && !IsCompleting && !IsComplete)
            {
                CurrentStep--;
                ValidationMessage = null;
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(CanGoBack));
            }
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public async Task<bool> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _navigationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsComplete)
            {
                return true;
            }

            ValidationMessage = ValidateAll();
            if (ValidationMessage is not null)
            {
                return false;
            }

            IsCompleting = true;
            var previousStartupEnabled = _startupRegistration.IsEnabled;
            var startupChanged = previousStartupEnabled != LaunchAtSignIn;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (startupChanged)
                {
                    await _startupRegistration.SetEnabledAsync(LaunchAtSignIn, cancellationToken);
                }

                var profile = new Profile(RecipientName.Trim(), OnboardingComplete: true);
                var preferences = new Preferences(
                    Theme,
                    new QuietHours(QuietHoursEnabled, QuietHoursStart, QuietHoursEnd),
                    ReducedMotion,
                    LocalNoteDailyLimit,
                    LaunchAtSignIn,
                    AlwaysOnTop: false,
                    HidePetDuringFullscreen,
                    AmbientMinimumInterval: TimeSpan.FromMinutes(15));
                var placement = new PetPlacement(
                    _monitorDeviceName,
                    PlacementX,
                    PlacementY,
                    MonitorPlacementService.ClampScale(PlacementScale));

                await _unitOfWork.ExecuteAsync(async (context, token) =>
                {
                    await context.Profiles.SaveAsync(profile, token);
                    await context.Preferences.SaveAsync(preferences, token);
                    await context.PetPlacements.SaveAsync(placement, token);
                }, cancellationToken);

                IsComplete = true;
                ValidationMessage = null;
                OnPropertyChanged(nameof(CanGoBack));
                return true;
            }
            catch
            {
                if (startupChanged)
                {
                    try
                    {
                        await _startupRegistration.SetEnabledAsync(
                            previousStartupEnabled,
                            CancellationToken.None);
                    }
                    catch
                    {
                        // The database transaction remains the source of truth;
                        // a later settings visit can reconcile the shortcut.
                    }
                }

                throw;
            }
            finally
            {
                IsCompleting = false;
            }
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _navigationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private string? ValidateCurrentStep() => CurrentStep switch
    {
        OnboardingStep.Recipient => ValidateRecipient(),
        OnboardingStep.Appearance => null,
        OnboardingStep.QuietHours => ValidateQuietHours(),
        OnboardingStep.Reminders => ValidateReminderDefaults(),
        OnboardingStep.Placement => ValidatePlacement(),
        OnboardingStep.Pairing => null,
        _ => "Choose a next step to continue.",
    };

    private string? ValidateAll() => ValidateRecipient()
        ?? ValidateQuietHours()
        ?? ValidateReminderDefaults()
        ?? ValidatePlacement();

    private string? ValidateRecipient()
    {
        var value = RecipientName.Trim();
        return value.Length is < 1 or > 80 || value.Any(char.IsControl)
            ? "Enter a name between 1 and 80 characters."
            : null;
    }

    private string? ValidateQuietHours() => QuietHoursEnabled && QuietHoursStart == QuietHoursEnd
        ? "Quiet hours need a start and end time."
        : null;

    private string? ValidateReminderDefaults() => LocalNoteDailyLimit is < 0 or > 12
        ? "Choose a daily note limit from 0 to 12."
        : null;

    private string? ValidatePlacement() => string.IsNullOrWhiteSpace(_monitorDeviceName)
        || !double.IsFinite(PlacementX)
        || !double.IsFinite(PlacementY)
        || PlacementX is < 0 or > 1
        || PlacementY is < 0 or > 1
        || !double.IsFinite(PlacementScale)
        || PlacementScale is < MonitorPlacementService.MinimumScale or > MonitorPlacementService.MaximumScale
        ? "Choose a safe pet position and size."
        : null;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OnboardingViewModel));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(CurrentStep) or nameof(IsCompleting) or nameof(IsComplete))
        {
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
