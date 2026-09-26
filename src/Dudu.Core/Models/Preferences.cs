namespace Dudu.Core.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record QuietHours(bool Enabled, TimeOnly Start, TimeOnly End);

public sealed record Preferences(
    AppTheme Theme,
    QuietHours QuietHours,
    bool ReducedMotion,
    int LocalNoteDailyLimit,
    bool LaunchAtSignIn,
    bool AlwaysOnTop,
    bool HidePetDuringFullscreen,
    TimeSpan AmbientMinimumInterval,
    bool HydrationRemindersEnabled = true,
    bool BreakRemindersEnabled = true,
    string? OutfitKey = null,
    bool AutomaticSeasonalMode = true,
    MonthDay? Anniversary = null,
    MonthDay? Birthday = null,
    bool EveningCheckInEnabled = false,
    bool BedtimeRitualEnabled = false,
    bool SoundsEnabled = true,
    double SoundVolume = 0.35,
    string? GlobalShortcut = null)
{
    public static Preferences Default => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        ReducedMotion: false,
        LocalNoteDailyLimit: 3,
        LaunchAtSignIn: true,
        AlwaysOnTop: true,
        HidePetDuringFullscreen: true,
        AmbientMinimumInterval: TimeSpan.FromMinutes(15),
        SoundsEnabled: true,
        SoundVolume: 0.35);

    public static double ClampSoundVolume(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.35;

    /// <summary>Normalizes a persisted global show/hide shortcut. Null means
    /// "use the built-in default"; blank text is treated the same way so a
    /// cleared value never reaches hotkey parsing. The value itself is only
    /// validated where the hotkey is registered (the App layer), keeping Core
    /// free of any keyboard/UI dependency.</summary>
    public static string? NormalizeGlobalShortcut(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
