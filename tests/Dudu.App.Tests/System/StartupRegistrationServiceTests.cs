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
    public async Task Enable_recreates_shortcut_if_it_was_deleted_externally()
    {
        var startupDirectory = Path.Combine(
            Path.GetTempPath(),
            "dudu-startup-" + Guid.NewGuid().ToString("N"));
        var writer = new FileBackedWriter();
        await using var service = new StartupRegistrationService(
            "/opt/dudu/Dudu.exe",
            startupDirectory,
            writer);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await service.SetEnabledAsync(true, cancellationToken);
            File.Delete(service.ShortcutPath);

            await service.SetEnabledAsync(true, cancellationToken);

            Assert.Equal(2, writer.WriteCount);
            Assert.True(File.Exists(service.ShortcutPath));
        }
        finally
        {
            if (Directory.Exists(startupDirectory))
            {
                Directory.Delete(startupDirectory, recursive: true);
            }
        }
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
        var settings = CreateSettings(startup, repository, preferences);

        await settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken);

        Assert.True(settings.Current.LaunchAtSignIn);
        Assert.True(repository.LastSaved!.LaunchAtSignIn);
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task Enable_repository_failure_does_not_create_external_shortcut()
    {
        var writer = new FakeWriter();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-enable-repository-failure-" + Guid.NewGuid().ToString("N"),
            writer);
        var repository = new FakePreferencesRepository { FailSave = true };
        var settings = CreateSettings(startup, repository, CreatePreferences(false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken));

        Assert.False(settings.Current.LaunchAtSignIn);
        Assert.Null(repository.LastSaved);
        Assert.Empty(writer.Writes);
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public async Task Disable_repository_failure_leaves_shortcut_and_durable_preference_enabled()
    {
        var writer = new FakeWriter();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-disable-repository-failure-" + Guid.NewGuid().ToString("N"),
            writer);
        await startup.SetEnabledAsync(true, TestContext.Current.CancellationToken);
        var repository = new FakePreferencesRepository();
        var settings = CreateSettings(startup, repository, CreatePreferences(true));
        repository.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(false, TestContext.Current.CancellationToken));

        Assert.True(settings.Current.LaunchAtSignIn);
        Assert.True(startup.IsEnabled);
        Assert.Empty(writer.Deletes);
    }

    [Fact]
    public async Task Runtime_apply_failure_rolls_back_shared_startup_snapshot_and_storage()
    {
        var writer = new FakeWriter();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-runtime-failure-" + Guid.NewGuid().ToString("N"),
            writer);
        var repository = new FakePreferencesRepository();
        var original = CreatePreferences(false);
        var failNextApply = true;
        var coordinator = new PreferenceMutationCoordinator(
            original,
            repository,
            (_, _) =>
            {
                if (failNextApply)
                {
                    failNextApply = false;
                    throw new InvalidOperationException("runtime apply failed");
                }

                return Task.CompletedTask;
            });
        var settings = new StartupSettingsService(startup, coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken));

        Assert.Equal(original, settings.Current);
        Assert.Equal(original, repository.LastSaved);
        Assert.Empty(writer.Writes);
        Assert.False(settings.NeedsReconciliation);
    }

    [Fact]
    public async Task External_failure_keeps_durable_choice_and_retry_reconciles_shortcut()
    {
        var writer = new FakeWriter { FailDelete = true };
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-external-failure-" + Guid.NewGuid().ToString("N"),
            writer);
        await startup.SetEnabledAsync(true, TestContext.Current.CancellationToken);
        var repository = new FakePreferencesRepository();
        var settings = CreateSettings(startup, repository, CreatePreferences(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(false, TestContext.Current.CancellationToken));

        Assert.False(settings.Current.LaunchAtSignIn);
        Assert.False(repository.LastSaved!.LaunchAtSignIn);
        Assert.True(startup.IsEnabled);
        Assert.True(settings.NeedsReconciliation);

        writer.FailDelete = false;
        await settings.RetryStartupRegistrationAsync(TestContext.Current.CancellationToken);

        Assert.False(startup.IsEnabled);
        Assert.False(settings.NeedsReconciliation);
        Assert.Single(writer.Deletes);
    }

    [Fact]
    public async Task Enable_external_failure_keeps_desired_state_and_retry_converges()
    {
        var writer = new FakeWriter { FailWrite = true };
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-enable-external-failure-" + Guid.NewGuid().ToString("N"),
            writer);
        var repository = new FakePreferencesRepository();
        var settings = CreateSettings(startup, repository, CreatePreferences(false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken));

        Assert.True(settings.Current.LaunchAtSignIn);
        Assert.True(repository.LastSaved!.LaunchAtSignIn);
        Assert.True(settings.DesiredLaunchAtSignIn);
        Assert.True(settings.NeedsReconciliation);
        Assert.False(startup.IsEnabled);

        writer.FailWrite = false;
        await settings.RetryStartupRegistrationAsync(TestContext.Current.CancellationToken);

        Assert.True(startup.IsEnabled);
        Assert.False(settings.NeedsReconciliation);
        Assert.Null(settings.ReconciliationError);
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task Incomplete_profile_disable_failure_is_observable_and_retries_before_completion()
    {
        var writer = new FakeWriter();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            "/tmp/startup-incomplete-disable-" + Guid.NewGuid().ToString("N"),
            writer);
        await startup.SetEnabledAsync(true, TestContext.Current.CancellationToken);
        var settings = CreateSettings(
            startup,
            new FakePreferencesRepository(),
            CreatePreferences(true));
        writer.FailDelete = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.ReconcileExternalAsync(false, TestContext.Current.CancellationToken));

        Assert.True(settings.NeedsReconciliation);
        Assert.False(settings.DesiredLaunchAtSignIn);
        Assert.True(settings.Current.LaunchAtSignIn);
        Assert.True(startup.IsEnabled);

        writer.FailDelete = false;
        await settings.RetryStartupRegistrationAsync(TestContext.Current.CancellationToken);

        Assert.False(startup.IsEnabled);
        Assert.False(settings.NeedsReconciliation);
        Assert.Null(settings.ReconciliationError);
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_shortcut_write()
    {
        var writer = new BlockingWriter();
        var service = new StartupRegistrationService("/opt/Dudu.exe", "/tmp/startup", writer);

        var write = service.SetEnabledAsync(true, TestContext.Current.CancellationToken);
        await writer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = service.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        writer.Release.TrySetResult(true);
        await write;
        await dispose;

        Assert.True(service.IsEnabled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.SetEnabledAsync(false, TestContext.Current.CancellationToken));
    }

    private static Preferences CreatePreferences(bool launchAtSignIn) => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        false,
        3,
        launchAtSignIn,
        false,
        true,
        TimeSpan.FromMinutes(15));

    private static StartupSettingsService CreateSettings(
        StartupRegistrationService startup,
        IPreferencesRepository repository,
        Preferences preferences) =>
        new(startup, new PreferenceMutationCoordinator(preferences, repository));

    private sealed class FakeWriter : IStartupLinkWriter
    {
        public List<(string Path, string Target, string Arguments)> Writes { get; } = [];
        public List<string> Deletes { get; } = [];
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }

        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("simulated startup write failure");
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, $"{targetPath}\n{arguments}");
            Writes.Add((shortcutPath, targetPath, arguments));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDelete) throw new IOException("simulated startup delete failure");
            File.Delete(shortcutPath);
            Deletes.Add(shortcutPath);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingWriter : IStartupLinkWriter
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteAtomicAsync(
            string shortcutPath,
            string targetPath,
            string arguments,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FileBackedWriter : IStartupLinkWriter
    {
        public int WriteCount { get; private set; }

        public Task WriteAtomicAsync(
            string shortcutPath,
            string targetPath,
            string arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, $"{targetPath}\n{arguments}");
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(shortcutPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Preferences? LastSaved { get; private set; }
        public bool FailSave { get; set; }
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Preferences?>(LastSaved);

        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            if (FailSave) throw new InvalidOperationException("simulated preferences save failure");
            LastSaved = preferences;
            return Task.CompletedTask;
        }
    }
}
