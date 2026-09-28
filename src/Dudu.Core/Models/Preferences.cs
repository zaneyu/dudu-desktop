namespace Dudu.Core.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record Preferences(
    AppTheme Theme,
    bool ReducedMotion,
    bool LaunchAtSignIn,
    bool AlwaysOnTop,
    bool HidePetDuringFullscreen,
    TimeSpan AmbientMinimumInterval,
    bool SoundsEnabled = true,
    double SoundVolume = 0.35,
    string? PauseMode = null,
    DateTimeOffset? PauseExpiresUtc = null)
{
    public static Preferences Default => new(
        AppTheme.System,
        ReducedMotion: false,
        LaunchAtSignIn: true,
        AlwaysOnTop: true,
        HidePetDuringFullscreen: true,
        AmbientMinimumInterval: TimeSpan.FromMinutes(15),
        SoundsEnabled: true,
        SoundVolume: 0.35);

    public static double ClampSoundVolume(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.35;
}
