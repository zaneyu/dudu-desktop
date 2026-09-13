using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
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
    private string? _outfitKey;
    private bool _automaticSeasonalMode;
    private bool _alwaysOnTop;
    private bool _hideDuringFullscreen;
    private string _globalShortcut = "Ctrl+Alt+D";

    public AppearanceViewModel(
        CompanionFeatureContext context,
        Action<AppTheme>? applyShellTheme = null)
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
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(CancellationToken.None));
        SavePlacementCommand = new AsyncRelayCommand(() => SavePlacementAsync(CancellationToken.None));
        ApplyOutfitCommand = new AsyncRelayCommand(() => ApplyOutfitAsync(CancellationToken.None));
        SaveShortcutCommand = new AsyncRelayCommand(() => SaveShortcutAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SavePlacementCommand { get; }
    public IAsyncRelayCommand ApplyOutfitCommand { get; }
    public IAsyncRelayCommand SaveShortcutCommand { get; }
    public ObservableCollection<string> OutfitOptions { get; } = ["automatic", "base"];
    public ObservableCollection<string> MonitorOptions { get; } = [];
    // Preferences has no outfit/seasonal fields yet. Slice 3 must disable
    // persistent controls rather than presenting a saved value that vanishes.
    public bool CanPersistOutfit => false;
    public bool CanConfigureSeasonalMode => false;
    public string OutfitAvailabilityMessage => "outfit selection is unavailable until dudu can save outfit choices the current look will stay unchanged";

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
    public string? OutfitKey { get => _outfitKey; set => SetProperty(ref _outfitKey, value); }
    public bool AutomaticSeasonalMode { get => _automaticSeasonalMode; set => SetProperty(ref _automaticSeasonalMode, value); }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetProperty(ref _alwaysOnTop, value); }
    public bool HideDuringFullscreen { get => _hideDuringFullscreen; set => SetProperty(ref _hideDuringFullscreen, value); }
    public string GlobalShortcut { get => _globalShortcut; set => SetProperty(ref _globalShortcut, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            // Cached Settings pages are created after onboarding.  Rehydrate
            // from the coordinator each time the page is activated so a later
            // onboarding/defaults write is the single source of truth.
            var preferences = _context.CurrentPreferences;
            Theme = preferences.Theme;
            ReducedMotion = preferences.ReducedMotion;
            AlwaysOnTop = preferences.AlwaysOnTop;
            HideDuringFullscreen = preferences.HidePetDuringFullscreen;
            var placements = await _context.PetPlacements.ListAsync(cancellationToken);
            MonitorOptions.Clear();
            foreach (var placement in placements) MonitorOptions.Add(placement.MonitorDeviceName);
            if (MonitorOptions.Count == 0) MonitorOptions.Add(MonitorDeviceName);
            var selectedPlacement = placements.FirstOrDefault(item =>
                string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal))
                ?? placements.FirstOrDefault();
            MonitorDeviceName = selectedPlacement?.MonitorDeviceName ?? MonitorOptions[0];
            if (selectedPlacement is not null) PetScale = selectedPlacement.Scale;
        });
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
        }, "oki pet placement saved");

    public Task ApplyOutfitAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => Task.FromException(new NotSupportedException(
            "aiyo outfit selection not ready yet")));

    public Task SaveShortcutAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(GlobalShortcut)) throw new ArgumentException("aiyo enter a shortcut first", nameof(GlobalShortcut));
            await _context.SetGlobalShortcutAsync(GlobalShortcut.Trim(), cancellationToken);
        }, "oki shortcut active for this session");
}
