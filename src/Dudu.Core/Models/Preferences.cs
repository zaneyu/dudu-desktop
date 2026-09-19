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
    double SoundVolume = 0.35)
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
}
