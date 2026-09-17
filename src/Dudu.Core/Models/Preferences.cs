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
    MonthDay? Birthday = null)
{
    public static Preferences Default => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        ReducedMotion: false,
        LocalNoteDailyLimit: 3,
        LaunchAtSignIn: true,
        AlwaysOnTop: false,
        HidePetDuringFullscreen: true,
        AmbientMinimumInterval: TimeSpan.FromMinutes(15));
}
