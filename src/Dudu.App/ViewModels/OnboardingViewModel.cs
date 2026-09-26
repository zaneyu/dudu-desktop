using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;

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

    private readonly PreferenceMutationCoordinator _preferenceMutations;
    private readonly IProfileRepository _profileRepository;
    private readonly IPetPlacementRepository _petPlacementRepository;
    private readonly IAppUnitOfWork _unitOfWork;
    private readonly StartupSettingsService _startupSettings;
    private readonly IPairingService _pairingService;
    private readonly PetPlacement _initialPlacement;
    private readonly Func<Preferences, PetPlacement, CancellationToken, Task>? _runtimeApplier;
    private readonly Func<CancellationToken, Task<MonitorPlacementSnapshot>>? _placementCapture;
    private readonly Func<PetPlacement, CancellationToken, Task>? _placementPreviewer;
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
    private string? _runtimeApplyError;
    private string? _startupRegistrationError;
    private bool _quietHoursTextInvalid;

    /// <summary>Shown when a quiet-hours box holds text that is not a time.</summary>
    public const string QuietHoursTimeFormatMessage = "wait use a time like 22:00";

    public OnboardingViewModel(
        PreferenceMutationCoordinator preferenceMutations,
        IProfileRepository profileRepository,
        IPetPlacementRepository petPlacementRepository,
        IAppUnitOfWork unitOfWork,
        StartupSettingsService startupSettings,
        IPairingService pairingService,
        string? monitorDeviceName = null,
        PetPlacement? initialPlacement = null,
        Func<Preferences, PetPlacement, CancellationToken, Task>? runtimeApplier = null,
        Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null,
        Func<PetPlacement, CancellationToken, Task>? placementPreviewer = null)
    {
        _preferenceMutations = preferenceMutations ?? throw new ArgumentNullException(nameof(preferenceMutations));
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _petPlacementRepository = petPlacementRepository ?? throw new ArgumentNullException(nameof(petPlacementRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _startupSettings = startupSettings ?? throw new ArgumentNullException(nameof(startupSettings));
        _initialPlacement = initialPlacement ?? new PetPlacement(
            string.IsNullOrWhiteSpace(monitorDeviceName) ? DefaultMonitorDeviceName : monitorDeviceName.Trim(),
            0.8,
            0.8,
            1);
        _monitorDeviceName = _initialPlacement.MonitorDeviceName;
        _runtimeApplier = runtimeApplier;
        _placementX = _initialPlacement.NormalizedX;
        _placementY = _initialPlacement.NormalizedY;
        _placementScale = MonitorPlacementService.ClampScale(_initialPlacement.Scale);
        _placementCapture = placementCapture;
        _placementPreviewer = placementPreviewer;
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
    public string? RuntimeApplyError { get => _runtimeApplyError; private set => Set(ref _runtimeApplyError, value); }
    public string? StartupRegistrationError { get => _startupRegistrationError; private set => Set(ref _startupRegistrationError, value); }
    public StartupSettingsService StartupSettings => _startupSettings;
    public bool CanGoBack => CurrentStep > OnboardingStep.Recipient && !IsCompleting && !IsComplete;
    public string ProgressText => $"step {(int)CurrentStep + 1} of {StepCount}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _navigationGate.WaitAsync(cancellationToken);
        try
        {
            var preferences = _preferenceMutations.Current;
            Theme = preferences.Theme;
            ReducedMotion = preferences.ReducedMotion;
            QuietHoursEnabled = preferences.QuietHours.Enabled;
            QuietHoursStart = preferences.QuietHours.Start;
            QuietHoursEnd = preferences.QuietHours.End;
            LocalNoteDailyLimit = preferences.LocalNoteDailyLimit;
            HydrationRemindersEnabled = preferences.HydrationRemindersEnabled;
            BreakRemindersEnabled = preferences.BreakRemindersEnabled;
            LaunchAtSignIn = preferences.LaunchAtSignIn;
            HidePetDuringFullscreen = preferences.HidePetDuringFullscreen;

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
            _quietHoursTextInvalid = false;
            ValidationMessage = null;
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    /// <summary>Applies the quiet-hours text boxes to the draft. Text that is
    /// not a time is remembered as invalid -- instead of silently keeping the
    /// previous value and moving on -- so Next/finish report it and stay put
    /// until she fixes it.</summary>
    /// <returns>True when both boxes hold a valid time.</returns>
    public bool TrySetQuietHoursText(string? startText, string? endText)
    {
        ThrowIfDisposed();
        var startValid = TimeOnly.TryParse(startText, out var start);
        var endValid = TimeOnly.TryParse(endText, out var end);
        if (startValid) QuietHoursStart = start;
        if (endValid) QuietHoursEnd = end;
        _quietHoursTextInvalid = !(startValid && endValid);
        return !_quietHoursTextInvalid;
    }

    /// <summary>Applies the note-limit NumberBox value. A cleared NumberBox
    /// reports NaN, which used to round to 0 and silently turn local notes
    /// off; it now keeps the current limit instead.</summary>
    /// <returns>False when the value was not a number and was ignored.</returns>
    public bool TrySetLocalNoteDailyLimit(double value)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(value)) return false;
        LocalNoteDailyLimit = (int)Math.Round(value);
        return true;
    }

    public async Task PreviewPlacementAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_placementCapture is null || _placementPreviewer is null)
        {
            return;
        }

        var current = await _placementCapture(cancellationToken);
        var preview = current.Placement with
        {
            Scale = MonitorPlacementService.ClampScale(PlacementScale),
        };
        await _placementPreviewer(preview, cancellationToken);
    }

    public async Task UseRecommendedPlacementAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PlacementX = 0.8;
        PlacementY = 0.8;
        PlacementScale = 1.0;
        if (_placementCapture is null || _placementPreviewer is null)
        {
            return;
        }

        var current = await _placementCapture(cancellationToken);
        await _placementPreviewer(
            current.Placement with { NormalizedX = 0.8, NormalizedY = 0.8, Scale = 1.0 },
            cancellationToken);
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
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capturedPlacement = _placementCapture is null
                    ? null
                    : await _placementCapture(cancellationToken);
                var profile = new Profile(RecipientName.Trim(), OnboardingComplete: true);
                var placement = capturedPlacement?.Placement
                    ?? new PetPlacement(
                        _monitorDeviceName,
                        PlacementX,
                        PlacementY,
                        MonitorPlacementService.ClampScale(PlacementScale));

                await _preferenceMutations.CommitAsync(
                    current => current with
                    {
                        Theme = Theme,
                        QuietHours = new QuietHours(QuietHoursEnabled, QuietHoursStart, QuietHoursEnd),
                        ReducedMotion = ReducedMotion,
                        LocalNoteDailyLimit = LocalNoteDailyLimit,
                        LaunchAtSignIn = LaunchAtSignIn,
                        HidePetDuringFullscreen = HidePetDuringFullscreen,
                        HydrationRemindersEnabled = HydrationRemindersEnabled,
                        BreakRemindersEnabled = BreakRemindersEnabled,
                    },
                    async (_, updated, token) =>
                    {
                        await _unitOfWork.ExecuteAsync(async (context, transactionToken) =>
                        {
                            await context.Profiles.SaveAsync(profile, transactionToken);
                            await context.Preferences.SaveAsync(updated, transactionToken);
                            await context.PetPlacements.SaveAsync(placement, transactionToken);
                            if (context.Reminders is IReminderWriter reminderWriter)
                            {
                                foreach (var reminder in LocalReminderDefaults.Create(
                                    updated,
                                    DateTimeOffset.UtcNow,
                                    TimeZoneInfo.Local))
                                {
                                    await reminderWriter.SaveAsync(reminder, transactionToken);
                                }
                            }
                        }, token);
                    },
                    cancellationToken);

                IsComplete = true;
                ValidationMessage = null;
                OnPropertyChanged(nameof(CanGoBack));

                try
                {
                    await _startupSettings.ReconcileAuthoritativeAsync(cancellationToken);
                    StartupRegistrationError = null;
                }
                catch (Exception exception)
                {
                    StartupRegistrationError = "aiyo setup saved but registration failed retry later";
                    Trace.TraceError("Dudu startup registration failed after commit: {0}", exception);
                }

                if (_runtimeApplier is not null)
                {
                    try
                    {
                        await _preferenceMutations.ApplyCurrentAsync(
                            (current, token) => _runtimeApplier(current, placement, token),
                            cancellationToken);
                        RuntimeApplyError = null;
                    }
                    catch (Exception exception)
                    {
                        RuntimeApplyError = "oh no saved live apply failed retry settings";
                        Trace.TraceError("Dudu runtime settings apply failed after commit: {0}", exception);
                    }
                }

                return true;
            }
            catch { throw; }
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

    public async Task RetryStartupRegistrationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _startupSettings.RetryStartupRegistrationAsync(cancellationToken);
        StartupRegistrationError = null;
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
        _ => "choose next step ah",
    };

    private string? ValidateAll() => ValidateRecipient()
        ?? ValidateQuietHours()
        ?? ValidateReminderDefaults()
        ?? ValidatePlacement();

    private string? ValidateRecipient()
    {
        var value = RecipientName.Trim();
        return value.Length is < 1 or > 80 || value.Any(char.IsControl)
            ? "cannot name must be 1 to 80 chars"
            : null;
    }

    private string? ValidateQuietHours() => _quietHoursTextInvalid
        ? QuietHoursTimeFormatMessage
        : QuietHoursEnabled && QuietHoursStart == QuietHoursEnd
            ? "alala quiet hours need start and end"
            : null;

    private string? ValidateReminderDefaults() => LocalNoteDailyLimit is < 0 or > 12
        ? "wait note limit must be 0 to 12"
        : null;

    private string? ValidatePlacement() => string.IsNullOrWhiteSpace(_monitorDeviceName)
        || !double.IsFinite(PlacementX)
        || !double.IsFinite(PlacementY)
        || PlacementX is < 0 or > 1
        || PlacementY is < 0 or > 1
        || !double.IsFinite(PlacementScale)
        || PlacementScale is < MonitorPlacementService.MinimumScale or > MonitorPlacementService.MaximumScale
        ? "aiyo pick a safe pet spot"
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
