using Dudu.App.Notifications;
using Dudu.App.System;
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
