using Dudu.App.Hosting;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class CompanionLaunchOptionsTests
{
    [Fact]
    public void Background_launch_still_opens_settings_until_profile_is_complete()
    {
        var options = CompanionLaunchOptions.Parse("--background");

        Assert.True(options.ShouldOpenSettings(null));
        Assert.True(options.ShouldOpenSettings(new Profile("Mia", false)));
        Assert.False(options.ShouldOpenSettings(new Profile("Mia", true)));
    }

    [Fact]
    public void Background_launch_only_shows_overlay_for_a_completed_profile_without_startup_policy()
    {
        var options = CompanionLaunchOptions.Parse("--background");
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(true, new TimeOnly(22), new TimeOnly(7)),
            false,
            3,
            LaunchAtSignIn: true,
            AlwaysOnTop: false,
            HidePetDuringFullscreen: true,
            TimeSpan.FromMinutes(15));

        Assert.False(options.ShouldShowOverlay(preferences, new Profile("Mia", false)));
        Assert.False(options.ShouldShowOverlay(preferences, new Profile("Mia", true)));
        Assert.True(options.ShouldShowOverlay(
            preferences with { LaunchAtSignIn = false },
            new Profile("Mia", true)));
    }
}
