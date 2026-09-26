using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Assets;
using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

public sealed class AppearanceViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private readonly Action<AppTheme> _applyShellTheme;
    private AppTheme _theme;
    private bool _reducedMotion;
    private double _petScale;
    private double _loadedPetScale;
    private string _monitorDeviceName;
    private IReadOnlyList<PetPlacement> _placements = [];
    private string _selectedOutfit = "automatic";
    private bool _automaticSeasonalMode = true;
    private DateTimeOffset? _anniversaryDate;
    private DateTimeOffset? _birthdayDate;
    private bool _alwaysOnTop;
    private bool _hideDuringFullscreen;
    private bool _soundsEnabled;
    private double _soundVolume;
    private string _globalShortcut = "Ctrl+Alt+D";

    public AppearanceViewModel(
        CompanionFeatureContext context,
        Action<AppTheme>? applyShellTheme = null,
        IReadOnlyList<string>? availableOutfitKeys = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
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
        OutfitOptions = new ObservableCollection<string>(
            ["automatic", ..(availableOutfitKeys ?? ["base"])
                .Where(key => !string.Equals(key, "automatic", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)]);
        SaveCommand = new AsyncRelayCommand((CancellationToken ct) => SaveAsync(ct));
        SavePlacementCommand = new AsyncRelayCommand((CancellationToken ct) => SavePlacementAsync(ct));
        ApplyOutfitCommand = new AsyncRelayCommand((CancellationToken ct) => ApplyOutfitAsync(ct));
        SaveShortcutCommand = new AsyncRelayCommand((CancellationToken ct) => SaveShortcutAsync(ct));
    }

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SavePlacementCommand { get; }
    public IAsyncRelayCommand ApplyOutfitCommand { get; }
    public IAsyncRelayCommand SaveShortcutCommand { get; }
    public ObservableCollection<string> OutfitOptions { get; }
    public ObservableCollection<string> MonitorOptions { get; } = [];
    public bool CanPersistOutfit => OutfitOptions.Count > 1;
    public bool CanConfigureSeasonalMode => OutfitOptions.Count > 1;
    public string OutfitAvailabilityMessage => BuildOutfitAvailabilityMessage();

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
    public double PetScale { get => _petScale; set => SetProperty(ref _petScale, Math.Clamp(value, 0.5, 2)); }
    public string MonitorDeviceName
    {
        get => _monitorDeviceName;
        set
        {
            // The monitor ComboBox binds SelectedItem TwoWay: when RefreshAsync
            // clears MonitorOptions, it pushes null back in, which used to
            // reset the chosen monitor to the first one on every visit.
            if (string.IsNullOrWhiteSpace(value)) return;
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
    public string SelectedOutfit
    {
        get => _selectedOutfit;
        set
        {
            var normalized = OutfitOptions.Contains(value, StringComparer.Ordinal)
                ? value
                : "automatic";
            if (!SetProperty(ref _selectedOutfit, normalized)) return;
            var automatic = string.Equals(normalized, "automatic", StringComparison.Ordinal);
            if (_automaticSeasonalMode != automatic)
            {
                _automaticSeasonalMode = automatic;
                OnPropertyChanged(nameof(AutomaticSeasonalMode));
            }
            OnPropertyChanged(nameof(OutfitAvailabilityMessage));
        }
    }

    public bool AutomaticSeasonalMode
    {
        get => _automaticSeasonalMode;
        set
        {
            if (!SetProperty(ref _automaticSeasonalMode, value)) return;
            if (value && !string.Equals(_selectedOutfit, "automatic", StringComparison.Ordinal))
            {
                _selectedOutfit = "automatic";
                OnPropertyChanged(nameof(SelectedOutfit));
            }
            else if (!value && string.Equals(_selectedOutfit, "automatic", StringComparison.Ordinal))
            {
                _selectedOutfit = OutfitOptions.FirstOrDefault(
                    key => !string.Equals(key, "automatic", StringComparison.Ordinal)) ?? "automatic";
                OnPropertyChanged(nameof(SelectedOutfit));
            }
            OnPropertyChanged(nameof(OutfitAvailabilityMessage));
        }
    }

    public DateTimeOffset? AnniversaryDate
    {
        get => _anniversaryDate;
        set
        {
            if (SetProperty(ref _anniversaryDate, value))
            {
                OnPropertyChanged(nameof(OutfitAvailabilityMessage));
            }
        }
    }

    public DateTimeOffset? BirthdayDate
    {
        get => _birthdayDate;
        set
        {
            if (SetProperty(ref _birthdayDate, value))
            {
                OnPropertyChanged(nameof(OutfitAvailabilityMessage));
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
    public string GlobalShortcut { get => _globalShortcut; set => SetProperty(ref _globalShortcut, value); }

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
                _automaticSeasonalMode = preferences.AutomaticSeasonalMode;
                OnPropertyChanged(nameof(AutomaticSeasonalMode));
                _selectedOutfit = preferences.AutomaticSeasonalMode
                    ? "automatic"
                    : preferences.OutfitKey ?? "base";
                OnPropertyChanged(nameof(SelectedOutfit));
                AnniversaryDate = ToDate(preferences.Anniversary);
                BirthdayDate = ToDate(preferences.Birthday);
                MonitorOptions.Clear();
                foreach (var placement in placements) MonitorOptions.Add(placement.MonitorDeviceName);
                if (MonitorOptions.Count == 0) MonitorOptions.Add(MonitorDeviceName);
                var selectedPlacement = placements.FirstOrDefault(item =>
                    string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal))
                    ?? placements.FirstOrDefault();
                var monitor = selectedPlacement?.MonitorDeviceName ?? MonitorOptions[0];
                if (string.Equals(monitor, MonitorDeviceName, StringComparison.Ordinal))
                {
                    // Unchanged value: re-announce it anyway so the ComboBox,
                    // whose items were just rebuilt, selects it again.
                    OnPropertyChanged(nameof(MonitorDeviceName));
                }
                else
                {
                    MonitorDeviceName = monitor;
                }

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
                OutfitKey = AutomaticSeasonalMode ? null : SelectedOutfit,
                AutomaticSeasonalMode = AutomaticSeasonalMode,
                Anniversary = ToMonthDay(AnniversaryDate),
                Birthday = ToMonthDay(BirthdayDate),
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

    public Task ApplyOutfitAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.UpdatePreferencesAsync(current => current with
            {
                OutfitKey = AutomaticSeasonalMode ? null : SelectedOutfit,
                AutomaticSeasonalMode = AutomaticSeasonalMode,
                Anniversary = ToMonthDay(AnniversaryDate),
                Birthday = ToMonthDay(BirthdayDate),
            }, cancellationToken);
        }, "oki seasonal look saved");

    public Task SaveShortcutAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(GlobalShortcut)) throw new ArgumentException("wait type a shortcut first", nameof(GlobalShortcut));
            await _context.SetGlobalShortcutAsync(GlobalShortcut.Trim(), cancellationToken);
        }, "otayyy shortcut set");

    private string BuildOutfitAvailabilityMessage()
    {
        if (!AutomaticSeasonalMode)
        {
            return $"manual outfit · {SelectedOutfit}";
        }

        var selected = SeasonalOutfitPolicy.Select(
            LocalDate,
            new SeasonalDates(ToMonthDay(AnniversaryDate), ToMonthDay(BirthdayDate)),
            OutfitOptions.Where(key => !string.Equals(key, "automatic", StringComparison.Ordinal)));
        return $"automatic mode · {selected} today";
    }

    private DateOnly LocalDate =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            _context.Clock.UtcNow,
            _context.Clock.LocalTimeZone).DateTime);

    private static MonthDay? ToMonthDay(DateTimeOffset? date) =>
        date is { } value ? new MonthDay(value.Month, value.Day) : null;

    private static DateTimeOffset? ToDate(MonthDay? monthDay)
    {
        if (monthDay is not { } value || value.Month is < 1 or > 12 || value.Day is < 1 or > 31)
        {
            return null;
        }

        var year = DateTime.Now.Year;
        if (value.Month == 2 && value.Day == 29)
        {
            while (!DateTime.IsLeapYear(year))
            {
                year++;
            }
        }

        if (value.Day > DateTime.DaysInMonth(year, value.Month))
        {
            return null;
        }

        // Local noon with the local offset: midnight UTC displayed a day early
        // in the date pickers anywhere west of UTC. Noon stays on the same
        // calendar day for every real-world offset.
        var localNoon = new DateTime(year, value.Month, value.Day, 12, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
    }
}
