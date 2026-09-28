using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

/// <summary>The three onboarding steps: her name, the look (theme and
/// reduced motion), then pairing (check or skip).</summary>
public enum OnboardingStep
{
    /// <summary>"what should dudu call you?"</summary>
    Recipient = 0,
    /// <summary>The look: theme and reduced motion.</summary>
    Appearance = 1,
    Pairing = 2,
}

/// <summary>
/// Draft-only onboarding state. Nothing is written until CompleteAsync has
/// validated the whole draft and committed the profile, preferences, and
/// placement through one application unit of work. Launch at sign-in and
/// hide-during-fullscreen are not asked here: onboarding saves them on (the
/// long-standing defaults) and Settings changes them later.
/// </summary>
public sealed class OnboardingViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public const int StepCount = 3;
    public const string DefaultMonitorDeviceName = "PRIMARY";
    public const string RecipientNameMessage = "add a name for dudu to call u, up to 80 characters";
    private const double DefaultPlacementCoordinate = 0.8;

    private readonly PreferenceMutationCoordinator _preferenceMutations;
    private readonly IProfileRepository _profileRepository;
    private readonly IPetPlacementRepository _petPlacementRepository;
    private readonly IAppUnitOfWork _unitOfWork;
    private readonly StartupSettingsService _startupSettings;
    private readonly IPairingService _pairingService;
    private readonly PetPlacement _initialPlacement;
    private readonly Func<Preferences, PetPlacement, CancellationToken, Task>? _runtimeApplier;
    private readonly Func<CancellationToken, Task<MonitorPlacementSnapshot>>? _placementCapture;
    private readonly string _monitorDeviceName;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private bool _disposed;
    private string _recipientName = string.Empty;
    private AppTheme _theme = AppTheme.System;
    private bool _reducedMotion;
    private double _placementX = DefaultPlacementCoordinate;
    private double _placementY = DefaultPlacementCoordinate;
    private double _placementScale = 1.0;
    private OnboardingStep _currentStep;
    private PairingAvailability _pairingAvailability = PairingAvailability.Offline;
    private bool _pairingSkipped = true;
    private bool _isCompleting;
    private bool _isComplete;
    private string? _validationMessage;
    private string? _runtimeApplyError;
    private string? _startupRegistrationError;

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
        Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null)
    {
        _preferenceMutations = preferenceMutations ?? throw new ArgumentNullException(nameof(preferenceMutations));
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _petPlacementRepository = petPlacementRepository ?? throw new ArgumentNullException(nameof(petPlacementRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _startupSettings = startupSettings ?? throw new ArgumentNullException(nameof(startupSettings));
        _initialPlacement = initialPlacement ?? new PetPlacement(
            string.IsNullOrWhiteSpace(monitorDeviceName) ? DefaultMonitorDeviceName : monitorDeviceName.Trim(),
            DefaultPlacementCoordinate,
            DefaultPlacementCoordinate,
            1);
        _monitorDeviceName = _initialPlacement.MonitorDeviceName;
        _runtimeApplier = runtimeApplier;
        _placementX = _initialPlacement.NormalizedX;
        _placementY = _initialPlacement.NormalizedY;
        _placementScale = MonitorPlacementService.ClampScale(_initialPlacement.Scale);
        _placementCapture = placementCapture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RecipientName { get => _recipientName; set => Set(ref _recipientName, value ?? string.Empty); }
    public AppTheme Theme { get => _theme; set => Set(ref _theme, value); }
    public bool ReducedMotion { get => _reducedMotion; set => Set(ref _reducedMotion, value); }
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

            var profile = await _profileRepository.GetAsync(cancellationToken);
            if (profile is not null)
            {
                RecipientName = profile.RecipientName;
                IsComplete = profile.OnboardingComplete;
            }

            var placement = await _petPlacementRepository.GetAsync(_monitorDeviceName, cancellationToken);
            if (placement is not null)
            {
                _placementX = placement.NormalizedX;
                _placementY = placement.NormalizedY;
                _placementScale = MonitorPlacementService.ClampScale(placement.Scale);
            }

            // The pairing step is the only reader of this probe. Every settings
            // open runs LoadAsync, and the probe is a live relay round trip
            // (plus registration on a device that has none yet): once setup is
            // done it only delayed the window and, when it threw, turned into
            // "aiyo couldnt load settings" -- the Connection page probes on its
            // own. During onboarding a failed probe means "pair later", never
            // "no settings".
            if (!IsComplete)
            {
                try
                {
                    PairingAvailability = await _pairingService.GetStateAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    PairingAvailability = PairingAvailability.Offline;
                    Trace.TraceWarning(
                        "Dudu onboarding pairing probe failed: {0} 0x{1:X8}",
                        exception.GetType().Name,
                        exception.HResult);
                }

                PairingSkipped = PairingAvailability == PairingAvailability.Offline;
            }

            OnPropertyChanged(nameof(CanGoBack));
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
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capturedPlacement = _placementCapture is null
                    ? null
                    : await _placementCapture(cancellationToken);
                var profile = new Profile(RecipientName.Trim(), OnboardingComplete: true);
                // Wherever Dudu sits now (she may have dragged it), else the
                // saved or default spot on this monitor.
                var placement = capturedPlacement?.Placement
                    ?? new PetPlacement(
                        _monitorDeviceName,
                        SafeCoordinate(_placementX),
                        SafeCoordinate(_placementY),
                        MonitorPlacementService.ClampScale(_placementScale));

                await _preferenceMutations.CommitAsync(
                    current => current with
                    {
                        Theme = Theme,
                        ReducedMotion = ReducedMotion,
                        LaunchAtSignIn = true,
                        HidePetDuringFullscreen = true,
                    },
                    async (_, updated, token) =>
                    {
                        await _unitOfWork.ExecuteAsync(async (context, transactionToken) =>
                        {
                            await context.Profiles.SaveAsync(profile, transactionToken);
                            await context.Preferences.SaveAsync(updated, transactionToken);
                            await context.PetPlacements.SaveAsync(placement, transactionToken);
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
        OnboardingStep.Pairing => null,
        _ => "choose next step ah",
    };

    private string? ValidateAll() => ValidateRecipient()
        ?? ValidatePlacement();

    private string? ValidateRecipient()
    {
        var value = RecipientName.Trim();
        return value.Length is < 1 or > 80 || value.Any(char.IsControl)
            ? RecipientNameMessage
            : null;
    }

    private string? ValidatePlacement() => string.IsNullOrWhiteSpace(_monitorDeviceName)
        ? "aiyo pick a safe pet spot"
        : null;

    /// <summary>A stored coordinate that is not a finite 0..1 fraction falls
    /// back to the default spot rather than blocking setup: there is no
    /// placement step to fix it from any more.</summary>
    private static double SafeCoordinate(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1 ? value : DefaultPlacementCoordinate;

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
