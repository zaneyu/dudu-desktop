using System.Text.RegularExpressions;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>UI/UX regressions on the Appearance page and its view model.</summary>
public sealed class AppearancePageUxTests
{
    [Fact]
    public void Pet_size_has_a_readable_value_label_that_follows_the_slider()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new AppearanceViewModel(fixture.Context);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("100%", viewModel.PetScaleLabel);

        viewModel.PetScale = 1.25;

        Assert.Equal("125%", viewModel.PetScaleLabel);
        Assert.Contains(nameof(AppearanceViewModel.PetScaleLabel), changed);
    }

    [Fact]
    public async Task Saved_anniversary_shows_on_the_same_day_west_of_utc()
    {
        var fixture = SettingsDataPagesFixture.Create(
            SettingsDataPagesFixture.UtcMinusFive,
            Preferences.Default with { Anniversary = new MonthDay(2, 14), Birthday = new MonthDay(2, 29) });
        var viewModel = new AppearanceViewModel(fixture.Context);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        // CalendarDatePicker renders the instant in the PC's zone; midnight UTC would show 13 Feb.
        var anniversary = Assert.NotNull(viewModel.AnniversaryDate);
        var shownLocally = TimeZoneInfo.ConvertTime(anniversary, SettingsDataPagesFixture.UtcMinusFive);
        Assert.Equal(2, shownLocally.Month);
        Assert.Equal(14, shownLocally.Day);
        var birthday = Assert.NotNull(viewModel.BirthdayDate);
        var birthdayLocally = TimeZoneInfo.ConvertTime(birthday, SettingsDataPagesFixture.UtcMinusFive);
        Assert.Equal(2, birthdayLocally.Month);
        Assert.Equal(29, birthdayLocally.Day);

        // And saving it straight back must not drift the stored month/day.
        await viewModel.ApplyOutfitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new MonthDay(2, 14), fixture.Preferences.Current?.Anniversary);
        Assert.Equal(new MonthDay(2, 29), fixture.Preferences.Current?.Birthday);
    }

    [Fact]
    public async Task Monitor_selection_survives_the_combo_box_pushing_null_while_options_are_repopulated()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("first monitor", 0.3, 0.4, 1.0), ct);
        await fixture.Placements.SaveAsync(new PetPlacement("second monitor", 0.5, 0.5, 1.8), ct);
        var viewModel = new AppearanceViewModel(fixture.Context);
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
        Assert.Contains(nameof(AppearanceViewModel.MonitorDeviceName), changed);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+Alt")]
    public async Task Malformed_shortcut_explains_the_expected_shape_and_is_not_applied(string shortcut)
    {
        var applied = new List<string>();
        var fixture = SettingsDataPagesFixture.Create(setGlobalShortcutAsync: (value, _) =>
        {
            applied.Add(value);
            return Task.CompletedTask;
        });
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = shortcut };

        await viewModel.SaveShortcutAsync(TestContext.Current.CancellationToken);

        Assert.Empty(applied);
        Assert.Equal("use ctrl alt shift or win plus one key like Ctrl+Alt+D", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Valid_shortcut_is_applied_and_shown_in_its_canonical_form()
    {
        var applied = new List<string>();
        var fixture = SettingsDataPagesFixture.Create(setGlobalShortcutAsync: (value, _) =>
        {
            applied.Add(value);
            return Task.CompletedTask;
        });
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = " ctrl + alt + k " };

        await viewModel.SaveShortcutAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(["Ctrl+Alt+K"], applied);
        Assert.Equal("Ctrl+Alt+K", viewModel.GlobalShortcut);
        Assert.Equal("otayyy shortcut set", viewModel.StatusMessage);
    }

    [Fact]
    public void Sliders_step_in_fine_increments_and_value_labels_are_announced()
    {
        var xaml = File.ReadAllText(Path.Combine(
            ConnectionPageUxTests.FindRepositoryRoot(), "src", "Dudu.App", "Pages", "AppearancePage.xaml"));

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
