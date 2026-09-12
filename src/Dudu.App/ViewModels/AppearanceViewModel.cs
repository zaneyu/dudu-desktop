using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

public sealed class AppearanceViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private AppTheme _theme;
    private bool _reducedMotion;
    private double _petScale;
    private string _monitorDeviceName;
    private string? _outfitKey;
    private bool _automaticSeasonalMode;
    private bool _alwaysOnTop;
    private bool _hideDuringFullscreen;
    private string _globalShortcut = "Ctrl+Alt+D";

    public AppearanceViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var preferences = context.InitialPreferences;
        _theme = preferences.Theme;
        _reducedMotion = preferences.ReducedMotion;
        _alwaysOnTop = preferences.AlwaysOnTop;
        _hideDuringFullscreen = preferences.HidePetDuringFullscreen;
        _petScale = 1;
        _monitorDeviceName = "Current monitor";
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(CancellationToken.None));
        SavePlacementCommand = new AsyncRelayCommand(() => SavePlacementAsync(CancellationToken.None));
        ApplyOutfitCommand = new AsyncRelayCommand(() => ApplyOutfitAsync(CancellationToken.None));
        SaveShortcutCommand = new AsyncRelayCommand(() => SaveShortcutAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SavePlacementCommand { get; }
    public IAsyncRelayCommand ApplyOutfitCommand { get; }
    public IAsyncRelayCommand SaveShortcutCommand { get; }
    public ObservableCollection<string> OutfitOptions { get; } = ["Automatic", "Base"];
    public ObservableCollection<string> MonitorOptions { get; } = [];

    public AppTheme Theme { get => _theme; set => SetProperty(ref _theme, value); }
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
            MonitorOptions.Clear();
            foreach (var placement in await _context.PetPlacements.ListAsync(cancellationToken)) MonitorOptions.Add(placement.MonitorDeviceName);
            if (MonitorOptions.Count == 0) MonitorOptions.Add(MonitorDeviceName);
            MonitorDeviceName = MonitorOptions[0];
        });
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var current = _context.InitialPreferences;
            var updated = current with
            {
                Theme = Theme,
                ReducedMotion = ReducedMotion,
                AlwaysOnTop = AlwaysOnTop,
                HidePetDuringFullscreen = HideDuringFullscreen,
            };
            await _context.Preferences.SaveAsync(updated, cancellationToken);
            await _context.ApplyPreferencesAsync(updated, cancellationToken);
        }, "Appearance saved.");

    public Task SavePlacementAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var existing = (await _context.PetPlacements.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.MonitorDeviceName, MonitorDeviceName, StringComparison.Ordinal));
            var current = (existing ?? new PetPlacement(MonitorDeviceName, 0.8, 0.8, PetScale)) with { Scale = PetScale };
            await _context.PetPlacements.SaveAsync(current, cancellationToken);
            await _context.ApplyPlacementAsync(current, cancellationToken);
        }, "Pet placement saved.");

    public Task ApplyOutfitAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () => await _context.ApplyOutfitAsync(
            OutfitKey is "Automatic" ? null : OutfitKey,
            cancellationToken), "Outfit updated.");

    public Task SaveShortcutAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(GlobalShortcut)) throw new ArgumentException("Enter a shortcut.", nameof(GlobalShortcut));
            await _context.SetGlobalShortcutAsync(GlobalShortcut.Trim(), cancellationToken);
        }, "Shortcut saved.");
}
