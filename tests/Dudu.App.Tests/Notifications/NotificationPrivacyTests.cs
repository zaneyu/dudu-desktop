using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Notifications;

public sealed class NotificationPrivacyTests
{
    [Fact]
    public async Task Remote_notification_never_contains_ciphertext_or_plaintext()
    {
        var sink = new RecordingNotificationSink();
        var service = new AppNotificationService(sink);

        await service.ShowRemoteNoteArrivalAsync(
            messageId: Guid.Parse("11111111-1111-4111-8111-111111111111"),
            TestContext.Current.CancellationToken);

        var notification = Assert.Single(sink.Items);
        Assert.Equal("A note arrived 💌", notification.Title);
        Assert.Null(notification.Body);
        Assert.Equal(
            "action=open-note&messageId=11111111-1111-4111-8111-111111111111",
            notification.ActivationArguments);
    }

    [Fact]
    public async Task Reminder_notification_carries_only_the_reminder_title_and_done_snooze_buttons()
    {
        var sink = new RecordingNotificationSink();
        var service = new AppNotificationService(sink);

        await service.ShowReminderAsync(
            "reminder-1",
            "Stretch",
            TestContext.Current.CancellationToken);

        var notification = Assert.Single(sink.Items);
        Assert.Equal("Stretch", notification.Title);
        Assert.Null(notification.Body);
        Assert.Collection(
            notification.Buttons,
            button =>
            {
                Assert.Equal("Done", button.Label);
                Assert.Equal("action=reminder-done&reminderId=reminder-1", button.ActivationArguments);
            },
            button =>
            {
                Assert.Equal("Snooze", button.Label);
                Assert.Equal("action=reminder-snooze&reminderId=reminder-1", button.ActivationArguments);
            });
    }

    [Fact]
    public async Task A_failed_registration_falls_back_to_not_attempting_a_toast()
    {
        var sink = new RecordingNotificationSink { RegistrationSucceeds = false };
        var service = new AppNotificationService(sink);

        await service.TryRegisterAsync(TestContext.Current.CancellationToken);
        await service.ShowRemoteNoteArrivalAsync(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            TestContext.Current.CancellationToken);

        Assert.False(service.NotificationsAvailable);
        Assert.Empty(sink.Items);
    }

    [Fact]
    public void Notification_and_presentation_source_never_reference_remote_envelope_or_crypto_types()
    {
        var forbidden = new[] { "RemoteEnvelope", "Ciphertext", "Plaintext", "Decrypt" };
        var sourceRoots = new[]
        {
            new[] { "src", "Dudu.App", "Notifications" },
            new[] { "src", "Dudu.App", "Presentation" },
        };

        foreach (var root in sourceRoots)
        {
            var directory = FindRepositoryDirectory(root);
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var contents = File.ReadAllText(file);
                foreach (var symbol in forbidden)
                {
                    Assert.DoesNotContain(symbol, contents, StringComparison.Ordinal);
                }
            }
        }
    }

    private static string FindRepositoryDirectory(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            relativePath.CopyTo(parts, 1);
            var path = Path.Combine(parts);
            if (Directory.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}

internal sealed class RecordingNotificationSink : INotificationSink
{
    private readonly List<NotificationRequest> _items = new();

    public bool RegistrationSucceeds { get; set; } = true;

    public IReadOnlyList<NotificationRequest> Items => _items;

    public Task<bool> TryRegisterAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RegistrationSucceeds);

    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        _items.Add(request);
        return Task.CompletedTask;
    }
}
