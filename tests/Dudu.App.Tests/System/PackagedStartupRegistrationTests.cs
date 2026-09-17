using Dudu.App.Notifications;
using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class PackagedStartupRegistrationTests
{
    [Fact]
    public async Task Packaged_executable_registers_only_the_current_user_startup_shortcut_with_background_argument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dudu-packaged-startup-{Guid.NewGuid():N}");
        var startup = Path.Combine(root, "CurrentUser", "Startup");
        var packagedExecutable = Path.Combine(root, "WindowsApps", "Dudu.App.exe");
        var writer = new RecordingWriter();
        try
        {
            await using var service = new StartupRegistrationService(packagedExecutable, startup, writer);

            await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

            var write = Assert.Single(writer.Writes);
            Assert.Equal(Path.Combine(startup, StartupRegistrationService.ShortcutFileName), write.ShortcutPath);
            Assert.Equal(Path.GetFullPath(packagedExecutable), write.TargetPath);
            Assert.Equal("--background", write.Arguments);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
