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
    private string _monitorDeviceName;
    private string _selectedOutfit = "automatic";
    private bool _automaticSeasonalMode = true;
    private DateTimeOffset? _anniversaryDate;
    private DateTimeOffset? _birthdayDate;
    private bool _alwaysOnTop;
    private bool _hideDuringFullscreen;
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
        _petScale = 1;
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
    public string MonitorDeviceName { get => _monitorDeviceName; set => SetProperty(ref _monitorDeviceName, value); }
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
            await MutateAsync(() =>
            {
                Theme = preferences.Theme;
                ReducedMotion = preferences.ReducedMotion;
                AlwaysOnTop = preferences.AlwaysOnTop;
                HideDuringFullscreen = preferences.HidePetDuringFullscreen;
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
                MonitorDeviceName = selectedPlacement?.MonitorDeviceName ?? MonitorOptions[0];
                if (selectedPlacement is not null) PetScale = selectedPlacement.Scale;
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
                OutfitKey = AutomaticSeasonalMode ? null : SelectedOutfit,
                AutomaticSeasonalMode = AutomaticSeasonalMode,
                Anniversary = ToMonthDay(AnniversaryDate),
                Birthday = ToMonthDay(BirthdayDate),
            }, cancellationToken);
            _applyShellTheme(Theme);
        }, "oki appearance saved");

    public Task SavePlacementAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var existing = (await _context.PetPlacements.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal));
            var current = (existing ?? new PetPlacement(MonitorDeviceName, 0.8, 0.8, PetScale)) with { Scale = PetScale };
            await _context.PetPlacements.SaveAsync(current, cancellationToken);
            await _context.ApplyPlacementAsync(current, cancellationToken);
        }, "okkk pet placement saved");

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
        var day = Math.Min(value.Day, DateTime.DaysInMonth(year, value.Month));
        return new DateTimeOffset(year, value.Month, day, 0, 0, 0, TimeSpan.Zero);
    }
}
