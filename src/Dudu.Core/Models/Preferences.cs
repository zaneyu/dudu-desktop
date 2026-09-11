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
    TimeSpan AmbientMinimumInterval);
