using Dudu.App.System;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public async Task Enable_is_atomic_current_user_only_and_idempotent()
    {
        var writer = new FakeWriter();
        await using var service = new StartupRegistrationService(
            "/opt/dudu/Dudu.exe",
            "/tmp/dudu-startup-" + Guid.NewGuid().ToString("N"),
            writer);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.SetEnabledAsync(true, cancellationToken);
        await service.SetEnabledAsync(true, cancellationToken);

        var entry = Assert.Single(writer.Writes);
        Assert.EndsWith(StartupRegistrationService.ShortcutFileName, entry.Path, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath("/opt/dudu/Dudu.exe"), entry.Target);
        Assert.Equal("--background", entry.Arguments);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public async Task Disable_removes_a_previously_enabled_shortcut_once()
    {
        var writer = new FakeWriter();
        await using var service = new StartupRegistrationService("/opt/Dudu.exe", "/tmp/startup", writer);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.SetEnabledAsync(true, cancellationToken);
        await service.SetEnabledAsync(false, cancellationToken);
        await service.SetEnabledAsync(false, cancellationToken);

        Assert.Single(writer.Deletes);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task Settings_service_persists_startup_setting_after_registration()
    {
        var writer = new FakeWriter();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-" + Guid.NewGuid().ToString("N"),
            writer);
        var repository = new FakePreferencesRepository();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, false, false, true, TimeSpan.FromMinutes(15));
        var settings = new StartupSettingsService(startup, repository, preferences);

        await settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken);

        Assert.True(settings.Current.LaunchAtSignIn);
        Assert.True(repository.LastSaved!.LaunchAtSignIn);
        Assert.Single(writer.Writes);
    }

    private sealed class FakeWriter : IStartupLinkWriter
    {
        public List<(string Path, string Target, string Arguments)> Writes { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((shortcutPath, targetPath, arguments));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deletes.Add(shortcutPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Preferences? LastSaved { get; private set; }
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Preferences?>(LastSaved);

        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }
    }
}
