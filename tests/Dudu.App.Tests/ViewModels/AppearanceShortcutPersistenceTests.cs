using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>
/// The Appearance page's shortcut used to be re-registered at runtime only: startup always
/// registered Ctrl+Alt+D, so after a restart the chosen shortcut silently reverted although the
/// page had said "shortcut set", and the page itself always opened showing Ctrl+Alt+D.
/// </summary>
public sealed class AppearanceShortcutPersistenceTests
{
    [Fact]
    public async Task Saving_a_valid_shortcut_registers_it_then_persists_it()
    {
        var ct = TestContext.Current.CancellationToken;
        SettingsDataPagesFixture? fixture = null;
        var persistedWhenRegistered = new List<string?>();
        fixture = SettingsDataPagesFixture.Create(setGlobalShortcutAsync: (value, _) =>
        {
            // Registration happens before anything is written.
            persistedWhenRegistered.Add(fixture!.Preferences.Current?.GlobalShortcut);
            return Task.CompletedTask;
        });
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = "ctrl + alt + k" };

        await viewModel.SaveShortcutAsync(ct);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal([null], persistedWhenRegistered);
        Assert.Equal("Ctrl+Alt+K", fixture.Preferences.Current?.GlobalShortcut);
        Assert.Equal("Ctrl+Alt+K", fixture.Context.CurrentPreferences.GlobalShortcut);
        Assert.Equal("otayyy shortcut set", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Saving_keeps_every_other_preference()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Preferences.Default with { Theme = AppTheme.Dark, SoundVolume = 0.8 };
        var fixture = SettingsDataPagesFixture.Create(
            initialPreferences: initial,
            setGlobalShortcutAsync: (_, _) => Task.CompletedTask);
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = "Ctrl+Shift+F9" };

        await viewModel.SaveShortcutAsync(ct);

        Assert.Equal(initial with { GlobalShortcut = "Ctrl+Shift+F9" }, fixture.Preferences.Current);
    }

    [Fact]
    public async Task A_refused_shortcut_is_not_persisted_and_says_which_one_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = SettingsDataPagesFixture.Create(
            initialPreferences: Preferences.Default with { GlobalShortcut = "Ctrl+Alt+J" },
            setGlobalShortcutAsync: (value, _) => Task.FromException(
                new HotkeyConflictException($"alala {value} already taken by another shortcut")));
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = "Ctrl+Alt+K" };

        await viewModel.SaveShortcutAsync(ct);

        Assert.Null(fixture.Preferences.Current);
        Assert.Equal("Ctrl+Alt+J", fixture.Context.CurrentPreferences.GlobalShortcut);
        Assert.Equal(
            "alala Ctrl+Alt+K already taken by another shortcut, still using Ctrl+Alt+J",
            viewModel.ErrorMessage);
        Assert.Null(viewModel.StatusMessage);
    }

    [Fact]
    public async Task Choosing_the_default_again_stores_no_custom_shortcut()
    {
        var ct = TestContext.Current.CancellationToken;
        var applied = new List<string>();
        var fixture = SettingsDataPagesFixture.Create(
            initialPreferences: Preferences.Default with { GlobalShortcut = "Ctrl+Alt+K" },
            setGlobalShortcutAsync: (value, _) =>
            {
                applied.Add(value);
                return Task.CompletedTask;
            });
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = "alt+ctrl+d" };

        await viewModel.SaveShortcutAsync(ct);

        Assert.Equal(["Ctrl+Alt+D"], applied);
        Assert.NotNull(fixture.Preferences.Current);
        Assert.Null(fixture.Preferences.Current!.GlobalShortcut);
        Assert.Equal("Ctrl+Alt+D", viewModel.GlobalShortcut);
    }

    [Theory]
    [InlineData(null, "Ctrl+Alt+D")]
    [InlineData("ctrl+shift+f9", "Ctrl+Shift+F9")]
    [InlineData("not a shortcut", "Ctrl+Alt+D")]
    [InlineData("Ctrl+C", "Ctrl+Alt+D")]
    public void The_page_opens_showing_the_stored_shortcut(string? stored, string expected)
    {
        var fixture = SettingsDataPagesFixture.Create(
            initialPreferences: Preferences.Default with { GlobalShortcut = stored });

        var viewModel = new AppearanceViewModel(fixture.Context);

        Assert.Equal(expected, viewModel.GlobalShortcut);
    }

    [Fact]
    public async Task Refresh_shows_the_current_stored_shortcut_and_drops_an_unsaved_edit()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = SettingsDataPagesFixture.Create(setGlobalShortcutAsync: (_, _) => Task.CompletedTask);
        var viewModel = new AppearanceViewModel(fixture.Context);
        Assert.Equal("Ctrl+Alt+D", viewModel.GlobalShortcut);

        // Another writer (e.g. a restore) changed the stored shortcut while the page was cached.
        await fixture.Context.UpdatePreferencesAsync(current => current with { GlobalShortcut = "Ctrl+Alt+K" }, ct);
        viewModel.GlobalShortcut = "Ctrl+Alt+Z";
        await viewModel.RefreshAsync(ct);

        Assert.Equal("Ctrl+Alt+K", viewModel.GlobalShortcut);
    }
}
