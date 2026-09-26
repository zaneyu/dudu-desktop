using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class PackagedStartupRegistrationTests
{
    [Fact]
    public async Task Default_construction_registers_the_process_path_in_current_user_startup_with_background_argument()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Startup registration uses the Windows current-user Startup folder.");
        }

        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        var writer = new RecordingWriter();
        await using var service = new StartupRegistrationService(writer: writer);
        Assert.True(service.IsAvailable, service.InitializationError);
        var expectedStartupShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupRegistrationService.ShortcutFileName);
        Assert.Equal(expectedStartupShortcut, service.ShortcutPath);
        if (File.Exists(service.ShortcutPath))
        {
            Assert.Skip("The current user already has a Dudu startup shortcut.");
        }

        await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

        var write = Assert.Single(writer.Writes);
        Assert.Equal(Path.GetFullPath(processPath!), service.InstalledExecutable);
        Assert.Equal(expectedStartupShortcut, write.ShortcutPath);
        Assert.Equal(Path.GetFullPath(processPath), write.TargetPath);
        Assert.Equal("--background", write.Arguments);
    }

    [Theory]
    [InlineData("action=open-note;messageId=11111111-1111-4111-8111-111111111111", NotificationActivationAction.OpenNote, "11111111-1111-4111-8111-111111111111", null)]
    [InlineData("action=reminder-done;reminderId=reminder-1", NotificationActivationAction.ReminderDone, null, "reminder-1")]
    [InlineData("action=reminder-snooze;reminderId=reminder-2", NotificationActivationAction.ReminderSnooze, null, "reminder-2")]
    public void Notification_activation_arguments_remain_package_identity_independent(
        string arguments,
        NotificationActivationAction expectedAction,
        string? expectedMessageId,
        string? expectedReminderId)
    {
        var activation = NotificationActivation.TryParse(arguments);

        Assert.NotNull(activation);
        Assert.Equal(expectedAction, activation!.Action);
        Assert.Equal(expectedMessageId, activation.MessageId);
        Assert.Equal(expectedReminderId, activation.ReminderId);
    }

    [Theory]
    [InlineData(StartupRegistrationBlockReason.DisabledByUser, StartupSettingsService.StartupDisabledInWindowsMessage)]
    [InlineData(StartupRegistrationBlockReason.DisabledByPolicy, StartupSettingsService.StartupDisabledByPolicyMessage)]
    public async Task Startup_switched_off_in_windows_shows_settings_guidance_instead_of_retry_forever(
        StartupRegistrationBlockReason reason,
        string expectedMessage)
    {
        // Once she switches Dudu off in Task Manager / Settings › Apps ›
        // Startup, RequestEnableAsync silently returns DisabledByUser every
        // time -- "aiyo startup registration needs another try" used to show
        // forever for something only she can change in Windows.
        var task = new BlockedPackagedStartupTask(reason);
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            Path.Combine(Path.GetTempPath(), "dudu-startup-blocked-" + Guid.NewGuid().ToString("N")),
            new RecordingWriter(),
            task);
        var settings = new StartupSettingsService(
            startup,
            new PreferenceMutationCoordinator(Preferences.Default, new InMemoryPreferencesRepository()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.RetryStartupRegistrationAsync(TestContext.Current.CancellationToken));

        Assert.True(settings.NeedsReconciliation);
        Assert.Equal(expectedMessage, settings.ReconciliationError);
        Assert.Contains("Settings › Apps › Startup", StartupSettingsService.StartupDisabledInWindowsMessage, StringComparison.Ordinal);
        Assert.True(startup.IsEnabledKnown);
        Assert.False(startup.IsEnabled);
        Assert.False(settings.ActualLaunchAtSignIn);

        // Once she switches it back on in Windows, the same retry succeeds.
        task.Blocked = false;
        await settings.RetryStartupRegistrationAsync(TestContext.Current.CancellationToken);
        Assert.False(settings.NeedsReconciliation);
        Assert.Null(settings.ReconciliationError);
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void Any_other_startup_failure_keeps_the_retry_message()
    {
        Assert.Equal(
            StartupSettingsService.RetryStartupMessage,
            StartupSettingsService.ReconciliationMessageFor(new InvalidOperationException("denied")));
        Assert.Equal(
            StartupSettingsService.StartupDisabledInWindowsMessage,
            StartupSettingsService.ReconciliationMessageFor(new InvalidOperationException(
                "wrapped",
                new StartupRegistrationBlockedException(StartupRegistrationBlockReason.DisabledByUser))));
    }

    private sealed class BlockedPackagedStartupTask(StartupRegistrationBlockReason reason)
        : IPackagedStartupTaskRegistration
    {
        public bool Blocked { get; set; } = true;

        public Task<bool> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled && Blocked)
            {
                throw new StartupRegistrationBlockedException(reason);
            }

            return Task.FromResult(enabled);
        }
    }

    private sealed class InMemoryPreferencesRepository : IPreferencesRepository
    {
        private Preferences? _saved;

        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_saved);

        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            _saved = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWriter : IStartupLinkWriter
    {
        public List<(string ShortcutPath, string TargetPath, string Arguments)> Writes { get; } = [];

        public Task WriteAtomicAsync(
            string shortcutPath,
            string targetPath,
            string arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((shortcutPath, targetPath, arguments));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
