using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>The settings window has three destinations: home, love notes and settings.
/// Reminders, tasks and focus, privacy and data are gone, and appearance and connection
/// were folded into settings.</summary>
public sealed class SettingsShellViewModelTests
{
    [Fact]
    public void Shell_exposes_exactly_home_notes_and_settings()
    {
        var shell = new SettingsShellViewModel();

        Assert.Equal(
            ["home", "notes", "settings"],
            shell.Destinations.Select(d => d.Tag).ToArray());
        Assert.Equal(
            ["NavHome", "NavLoveNotes", "NavSettings"],
            shell.Destinations.Select(d => d.AutomationId).ToArray());
    }

    [Theory]
    [InlineData("reminders")]
    [InlineData("tasks")]
    [InlineData("appearance")]
    [InlineData("connection")]
    [InlineData("privacy")]
    public void Removed_destinations_are_rejected(string tag)
    {
        var shell = new SettingsShellViewModel();

        Assert.False(shell.Navigate(tag));
        Assert.Equal("home", shell.CurrentDestination);
    }

    [Fact]
    public void Settings_destination_is_reachable()
    {
        var shell = new SettingsShellViewModel();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.True(shell.Navigate("settings"));

        Assert.Equal("settings", shell.CurrentDestination);
        Assert.Equal([nameof(SettingsShellViewModel.CurrentDestination)], changed);
        Assert.Equal("Settings", shell.GetDestination("settings").Title);
    }
}
