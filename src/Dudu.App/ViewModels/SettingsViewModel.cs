using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.App.System;
using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

/// <summary>The one Settings destination: look and motion, pet options,
/// the partner connection (the old Appearance page, with Connection folded in) and
/// "delete my data".</summary>
public sealed class SettingsViewModel : FeatureViewModelBase
{
    internal const string RemoteUnreachableMessage =
        "couldn't reach the partner server, so your partner can still send here. "
        + "try again when you're online, or wipe this pc only";
    internal const string WipedMessage = "all cleaned up -- restart dudu to start fresh";

    private readonly CompanionFeatureContext _context;
    private readonly Action<AppTheme> _applyShellTheme;
    private AppTheme _theme;
    private bool _reducedMotion;
    private double _petScale;
    private double _loadedPetScale;
    private string _monitorDeviceName;
    private IReadOnlyList<PetPlacement> _placements = [];
    private bool _alwaysOnTop;
    private bool _hideDuringFullscreen;
    private bool _soundsEnabled;
    private double _soundVolume;
    private bool _isDeleteConfirmVisible;
    private bool _isWipeThisPcOnlyVisible;
    private bool _deleteInProgress;

    public SettingsViewModel(
        CompanionFeatureContext context,
        Action<AppTheme>? applyShellTheme = null,
        ConnectionViewModel? connection = null,
        bool isSafeMode = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        IsSafeMode = isSafeMode;
        Connection = connection ?? new ConnectionViewModel(context);
        _applyShellTheme = applyShellTheme ?? (_ => { });
        var preferences = context.CurrentPreferences;
        _theme = preferences.Theme;
        _reducedMotion = preferences.ReducedMotion;
        _alwaysOnTop = preferences.AlwaysOnTop;
        _hideDuringFullscreen = preferences.HidePetDuringFullscreen;
        _soundsEnabled = preferences.SoundsEnabled;
        _soundVolume = Preferences.ClampSoundVolume(preferences.SoundVolume);
        _petScale = 1;
        _loadedPetScale = _petScale;
        _monitorDeviceName = "current monitor";
        SaveCommand = new AsyncRelayCommand((CancellationToken ct) => SaveAsync(ct));
        SavePlacementCommand = new AsyncRelayCommand((CancellationToken ct) => SavePlacementAsync(ct));
        RequestDeleteMyDataCommand = new RelayCommand(RequestDeleteMyData, CanStartDelete);
        CancelDeleteMyDataCommand = new RelayCommand(CancelDeleteMyData, CanStartDelete);
        ConfirmDeleteMyDataCommand = new AsyncRelayCommand(
            (CancellationToken ct) => DeleteMyDataAsync(ct),
            CanStartDelete);
        WipeThisPcOnlyCommand = new AsyncRelayCommand(
            (CancellationToken ct) => WipeThisPcOnlyAsync(ct),
            CanStartDelete);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsBusy)) return;
        NotifyDeleteCommandsChanged();
    }

    /// <summary>Safe mode (a crash loop or an unopenable database): the page shows only
    /// "delete my data" (this pc only, as safe mode wires it) and how to exit. There is no
    /// user-facing backup or restore; automatic database recovery is unchanged.</summary>
    public bool IsSafeMode { get; }

    /// <summary>Everything except "delete my data": hidden in safe mode.</summary>
    public bool ShowsFullSettings => !IsSafeMode;

    /// <summary>The "partner connection" section of the page.</summary>
    public ConnectionViewModel Connection { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SavePlacementCommand { get; }
    public IRelayCommand RequestDeleteMyDataCommand { get; }
    public IRelayCommand CancelDeleteMyDataCommand { get; }
    public IAsyncRelayCommand ConfirmDeleteMyDataCommand { get; }
    public IAsyncRelayCommand WipeThisPcOnlyCommand { get; }

    /// <summary>Shows the "are u sure" panel under the delete button.</summary>
    public bool IsDeleteConfirmVisible
    {
        get => _isDeleteConfirmVisible;
        private set => SetProperty(ref _isDeleteConfirmVisible, value);
    }

    /// <summary>Shown only after the partner server could not be reached: nothing was
    /// wiped, and this offers to clean up this pc alone.</summary>
    public bool IsWipeThisPcOnlyVisible
    {
        get => _isWipeThisPcOnlyVisible;
        private set => SetProperty(ref _isWipeThisPcOnlyVisible, value);
    }
    public ObservableCollection<string> MonitorOptions { get; } = [];

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value)) OnPropertyChanged(nameof(ThemeIndex));
        }
    }
    public int ThemeIndex
    {
        get => (int)Theme;
        set
        {
            if (Enum.IsDefined((AppTheme)value)) Theme = (AppTheme)value;
        }
    }
    public bool ReducedMotion { get => _reducedMotion; set => SetProperty(ref _reducedMotion, value); }
    public double PetScale
    {
        get => _petScale;
        set
        {
            if (SetProperty(ref _petScale, Math.Clamp(value, 0.5, 2))) OnPropertyChanged(nameof(PetScaleLabel));
        }
    }
    /// <summary>The pet-size slider had no readout at all (the volume slider does), so she
    /// could not tell what size she was picking or get back to the default 100%.</summary>
    public string PetScaleLabel => $"{Math.Round(PetScale * 100):0}%";
    public string MonitorDeviceName
    {
        get => _monitorDeviceName;
        set
        {
            // The monitor ComboBox's SelectedItem is bound TwoWay; RefreshAsync clearing
            // MonitorOptions makes the ComboBox push null back, which used to wipe the chosen
            // monitor so every revisit silently jumped back to the first one.
            if (value is null) return;
            if (!SetProperty(ref _monitorDeviceName, value)) return;
            // Each monitor can have its own saved pet size. Reload it from
            // whatever RefreshAsync already loaded into _placements -- no
            // async work here, and a monitor with no saved placement yet
            // just keeps the slider where it was.
            var placement = _placements.FirstOrDefault(item =>
                string.Equals(item.MonitorDeviceName, value, StringComparison.Ordinal));
            if (placement is not null)
            {
                PetScale = placement.Scale;
                _loadedPetScale = placement.Scale;
            }
        }
    }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetProperty(ref _alwaysOnTop, value); }
    public bool HideDuringFullscreen { get => _hideDuringFullscreen; set => SetProperty(ref _hideDuringFullscreen, value); }
    public bool SoundsEnabled { get => _soundsEnabled; set => SetProperty(ref _soundsEnabled, value); }
    public double SoundVolume
    {
        get => _soundVolume;
        set
        {
            var normalized = Preferences.ClampSoundVolume(value);
            if (SetProperty(ref _soundVolume, normalized)) OnPropertyChanged(nameof(SoundVolumeLabel));
        }
    }
    public string SoundVolumeLabel => $"{SoundVolume:P0}";
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            // Cached Settings pages are created after onboarding.  Rehydrate
            // from the coordinator each time the page is activated so a later
            // onboarding/defaults write is the single source of truth.
            var preferences = _context.CurrentPreferences;
            var placements = await _context.PetPlacements.ListAsync(ct);
            _placements = placements;
            await MutateAsync(() =>
            {
                Theme = preferences.Theme;
                ReducedMotion = preferences.ReducedMotion;
                AlwaysOnTop = preferences.AlwaysOnTop;
                HideDuringFullscreen = preferences.HidePetDuringFullscreen;
                SoundsEnabled = preferences.SoundsEnabled;
                SoundVolume = preferences.SoundVolume;
                MonitorOptions.Clear();
                foreach (var placement in placements) MonitorOptions.Add(placement.MonitorDeviceName);
                if (MonitorOptions.Count == 0) MonitorOptions.Add(MonitorDeviceName);
                var selectedPlacement = placements.FirstOrDefault(item =>
                    string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal))
                    ?? placements.FirstOrDefault();
                MonitorDeviceName = selectedPlacement?.MonitorDeviceName ?? MonitorOptions[0];
                // Re-announce even when the name did not change: repopulating the options left
                // the ComboBox with no selection, and an unchanged value raises no event.
                OnPropertyChanged(nameof(MonitorDeviceName));
                if (selectedPlacement is not null)
                {
                    PetScale = selectedPlacement.Scale;
                    _loadedPetScale = selectedPlacement.Scale;
                }
            }, ct);
        }, cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.UpdatePreferencesAsync(current => current with
            {
                Theme = Theme,
                ReducedMotion = ReducedMotion,
                AlwaysOnTop = AlwaysOnTop,
                HidePetDuringFullscreen = HideDuringFullscreen,
                SoundsEnabled = SoundsEnabled,
                SoundVolume = SoundVolume,
            }, cancellationToken);
            _applyShellTheme(Theme);
            // "save appearance" shares the same pet-size slider as "save pet
            // placement" (Scroll-to-resize on the pet itself commits through
            // the same PetPlacements path), so a scale the user just picked
            // is never silently dropped. It must not create a placement row
            // from scratch (that would teleport the pet onto a monitor it
            // has no saved position on) or re-apply/move the pet when the
            // slider was not touched -- only the explicit "save pet
            // placement" button does either of those.
            await SavePlacementCoreAsync(cancellationToken, createIfMissing: false);
        }, "oki appearance saved");

    public Task SavePlacementAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => SavePlacementCoreAsync(cancellationToken, createIfMissing: true), "okkk pet placement saved");

    /// <summary>Scale changes smaller than this are treated as "the user did not touch the
    /// slider" rather than a real edit, to absorb floating-point round-trip noise.</summary>
    private const double PetScaleEpsilon = 0.001;

    private async Task SavePlacementCoreAsync(CancellationToken cancellationToken, bool createIfMissing)
    {
        var existing = (await _context.PetPlacements.ListAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal));

        if (existing is null)
        {
            if (!createIfMissing) return;
            existing = new PetPlacement(MonitorDeviceName, 0.8, 0.8, PetScale);
        }
        else if (!createIfMissing
            && (Math.Abs(PetScale - _loadedPetScale) < PetScaleEpsilon
                || Math.Abs(existing.Scale - PetScale) < PetScaleEpsilon))
        {
            return;
        }

        var current = existing with { Scale = PetScale };
        await _context.PetPlacements.SaveAsync(current, cancellationToken);
        await _context.ApplyPlacementAsync(current, cancellationToken);
        _loadedPetScale = PetScale;
    }

    /// <summary>"Delete my data". With a relay configured it stops the sync loop, deletes
    /// the remote device first and only then wipes this pc; if the remote delete does not
    /// complete, the loop is restarted, nothing is wiped, and "wipe this pc only" is
    /// offered. Offline builds and safe mode wipe this pc only, without an error. Every
    /// step runs through RunAsync so nothing escapes into an async void XAML handler.</summary>
    public async Task DeleteMyDataAsync(CancellationToken cancellationToken = default)
    {
        if (_deleteInProgress) return;
        SetDeleteInProgress(true);
        try
        {
            IsDeleteConfirmVisible = false;
            IsWipeThisPcOnlyVisible = false;
            if (_context.RemoteDeleteAvailable)
            {
                var remoteDeleted = false;
                try
                {
                    if (!await RunAsync(() => _context.StopRemoteSyncAsync(cancellationToken)))
                    {
                        return; // RunAsync already reported the error
                    }

                    remoteDeleted = await RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken));
                }
                finally
                {
                    // Also on cancellation: the loop must never stay stopped when the
                    // device was not deleted, or partner notes silently stop arriving.
                    if (!remoteDeleted) await RestartRemoteSyncAsync();
                }

                if (!remoteDeleted)
                {
                    // Replaces RunAsync's text: the relay's own error may carry detail
                    // that does not belong on screen.
                    ReportError(RemoteUnreachableMessage);
                    IsWipeThisPcOnlyVisible = true;
                    return;
                }
            }

            await WipeLocalAsync(cancellationToken);
        }
        finally
        {
            SetDeleteInProgress(false);
        }
    }

    /// <summary>Wipes this pc only (the local half of "delete my data"). Offered after
    /// the partner server could not be reached.</summary>
    public async Task WipeThisPcOnlyAsync(CancellationToken cancellationToken = default)
    {
        if (_deleteInProgress) return;
        SetDeleteInProgress(true);
        try
        {
            IsDeleteConfirmVisible = false;
            await WipeLocalAsync(cancellationToken);
        }
        finally
        {
            SetDeleteInProgress(false);
        }
    }

    private async Task WipeLocalAsync(CancellationToken cancellationToken)
    {
        if (await RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), WipedMessage))
        {
            IsWipeThisPcOnlyVisible = false;
        }
    }

    private async Task RestartRemoteSyncAsync()
    {
        try
        {
            // Not the caller's token: a cancelled delete must still resume syncing.
            await _context.StartRemoteSyncAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu remote sync restart after delete failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    private void RequestDeleteMyData()
    {
        ClearMessages();
        IsWipeThisPcOnlyVisible = false;
        IsDeleteConfirmVisible = true;
    }

    private void CancelDeleteMyData()
    {
        ClearMessages();
        IsDeleteConfirmVisible = false;
    }

    private bool CanStartDelete() => !IsBusy && !_deleteInProgress;

    private void SetDeleteInProgress(bool value)
    {
        _deleteInProgress = value;
        NotifyDeleteCommandsChanged();
    }

    private void NotifyDeleteCommandsChanged()
    {
        // The constructor's base ObservableObject can raise before the commands exist.
        RequestDeleteMyDataCommand?.NotifyCanExecuteChanged();
        CancelDeleteMyDataCommand?.NotifyCanExecuteChanged();
        ConfirmDeleteMyDataCommand?.NotifyCanExecuteChanged();
        WipeThisPcOnlyCommand?.NotifyCanExecuteChanged();
    }
}
