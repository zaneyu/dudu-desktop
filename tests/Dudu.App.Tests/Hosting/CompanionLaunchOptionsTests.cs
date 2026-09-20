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

    [Theory]
    [InlineData("--self-test")]
    [InlineData("--SELF-TEST")]
    [InlineData("--background --self-test")]
    public void Self_test_flag_is_recognised_case_insensitively_and_alongside_background(string arguments)
    {
        var options = CompanionLaunchOptions.Parse(arguments);

        Assert.True(options.SelfTest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--background")]
    public void Self_test_flag_is_false_when_not_passed(string arguments)
    {
        var options = CompanionLaunchOptions.Parse(arguments);

        Assert.False(options.SelfTest);
    }

    [Fact]
    public void Background_launch_shows_overlay_only_for_a_completed_profile()
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
        Assert.True(options.ShouldShowOverlay(preferences, new Profile("Mia", true)));
        Assert.True(options.ShouldShowOverlay(
            preferences with { LaunchAtSignIn = false },
            new Profile("Mia", true)));
    }
}
