using System.Text.RegularExpressions;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>UI/UX regressions on the Settings page (look and motion) and its view model.</summary>
public sealed class SettingsPageUxTests
{
    [Fact]
    public void Pet_size_has_a_readable_value_label_that_follows_the_slider()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new SettingsViewModel(fixture.Context);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("100%", viewModel.PetScaleLabel);

        viewModel.PetScale = 1.25;

        Assert.Equal("125%", viewModel.PetScaleLabel);
        Assert.Contains(nameof(SettingsViewModel.PetScaleLabel), changed);
    }

    [Fact]
    public async Task Monitor_selection_survives_the_combo_box_pushing_null_while_options_are_repopulated()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("first monitor", 0.3, 0.4, 1.0), ct);
        await fixture.Placements.SaveAsync(new PetPlacement("second monitor", 0.5, 0.5, 1.8), ct);
        var viewModel = new SettingsViewModel(fixture.Context);
        await viewModel.RefreshAsync(ct);
        viewModel.MonitorDeviceName = "second monitor";

        // What the TwoWay SelectedItem binding does when MonitorOptions is cleared.
        viewModel.MonitorDeviceName = null!;
        Assert.Equal("second monitor", viewModel.MonitorDeviceName);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await viewModel.RefreshAsync(ct);

        Assert.Equal("second monitor", viewModel.MonitorDeviceName);
        Assert.Equal(1.8, viewModel.PetScale);
        // Re-announced so the ComboBox re-selects it after its items were rebuilt.
        Assert.Contains(nameof(SettingsViewModel.MonitorDeviceName), changed);
    }

    [Fact]
    public async Task Safe_mode_offers_only_delete_my_data_and_wipes_this_pc_only()
    {
        var calls = new List<string>();
        var fixture = SettingsDataPagesFixture.Create(
            deleteLocalDataAsync: _ =>
            {
                calls.Add("local");
                return Task.CompletedTask;
            },
            deleteRemoteDataAsync: _ =>
            {
                calls.Add("remote");
                return Task.CompletedTask;
            });
        var viewModel = new SettingsViewModel(fixture.Context, isSafeMode: true);

        Assert.True(viewModel.IsSafeMode);
        Assert.False(viewModel.ShowsFullSettings);
        Assert.False(new SettingsViewModel(fixture.Context).IsSafeMode);
        Assert.True(new SettingsViewModel(fixture.Context).ShowsFullSettings);

        viewModel.RequestDeleteMyDataCommand.Execute(null);
        await viewModel.ConfirmDeleteMyDataCommand.ExecuteAsync(null);

        // Safe mode has no relay wired (remote delete unavailable): local wipe only, no error.
        Assert.Equal(["local"], calls);
        Assert.False(viewModel.HasError);
        Assert.Equal(SettingsViewModel.WipedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public void Safe_mode_page_hides_everything_but_delete_my_data_and_has_no_backup_or_restore()
    {
        var root = SettingsPageConnectionTests.FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "SettingsPage.xaml"));

        var notice = xaml.IndexOf("Visibility=\"{x:Bind ViewModel.IsSafeMode}\"", StringComparison.Ordinal);
        var fullStart = xaml.IndexOf("<StackPanel Spacing=\"20\" Visibility=\"{x:Bind ViewModel.ShowsFullSettings}\">", StringComparison.Ordinal);
        var lookAndMotion = xaml.IndexOf("Text=\"look and motion\"", StringComparison.Ordinal);
        var connection = xaml.IndexOf("AutomationProperties.AutomationId=\"SettingsConnectionHeading\"", StringComparison.Ordinal);
        var startup = xaml.IndexOf("AutomationProperties.AutomationId=\"SettingsLaunchAtSignIn\"", StringComparison.Ordinal);
        var deleteMyData = xaml.IndexOf("AutomationProperties.AutomationId=\"SettingsDeleteMyData\"", StringComparison.Ordinal);
        Assert.True(notice > 0);
        Assert.True(fullStart > notice);
        // Every other section sits inside the panel hidden in safe mode; delete my data does not.
        var fullEnd = xaml.LastIndexOf("</StackPanel>", xaml.IndexOf("Text=\"your data\"", StringComparison.Ordinal), StringComparison.Ordinal);
        foreach (var section in new[] { lookAndMotion, connection, startup })
        {
            Assert.InRange(section, fullStart, fullEnd);
        }
        Assert.True(deleteMyData > fullEnd);
        Assert.Contains("close this window to exit dudu", xaml, StringComparison.Ordinal);

        foreach (var removed in new[] { "Backup\"", "Restore\"", "BackupCommand", "RestoreCommand", "restore from backup", "make a backup" })
        {
            Assert.DoesNotContain(removed, xaml, StringComparison.OrdinalIgnoreCase);
        }

        var window = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Windows", "SettingsWindow.xaml.cs"));
        Assert.Contains("isSafeMode: _context.IsSafeMode", window, StringComparison.Ordinal);
        Assert.Contains("RootNavigation.IsPaneVisible = false;", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Sliders_step_in_fine_increments_and_value_labels_are_announced()
    {
        var xaml = File.ReadAllText(Path.Combine(
            SettingsPageConnectionTests.FindRepositoryRoot(), "src", "Dudu.App", "Pages", "SettingsPage.xaml"));

        // Slider.StepFrequency defaults to 1, so without it dragging the 0..1 volume slider
        // could only land on 0% or 100%, and pet size only on 50%/150%/200%.
        var volume = Regex.Match(xaml, "<Slider[^>]*AppearanceSoundVolume\"[^>]*>").Value;
        var petSize = Regex.Match(xaml, "<Slider[^>]*AppearancePetScale\"[^>]*>").Value;
        Assert.Contains("StepFrequency=\"0.01\"", volume);
        Assert.Contains("StepFrequency=\"0.05\"", petSize);

        // A fixed AutomationProperties.Name overrides a TextBlock's text for screen readers,
        // so the value readouts must bind their name to the value itself.
        Assert.Contains("AutomationProperties.AutomationId=\"AppearanceSoundVolumeLabel\" AutomationProperties.Name=\"{x:Bind ViewModel.SoundVolumeLabel, Mode=OneWay}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"AppearancePetScaleLabel\" AutomationProperties.Name=\"{x:Bind ViewModel.PetScaleLabel, Mode=OneWay}\"", xaml);
    }
}
