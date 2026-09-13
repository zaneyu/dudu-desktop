using Dudu.Core.Abstractions;

namespace Dudu.App.Notifications;

/// <summary>
/// Lets <see cref="PresentationCoordinator"/> (and tests) attempt Windows
/// notification registration without depending on the concrete
/// <see cref="AppNotificationService"/> type.
/// </summary>
public interface IRegistrableNotificationService
{
    Task<bool> TryRegisterAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Converts high-level notification requests into <see cref="INotificationSink"/>
/// calls. The remote-note path takes only a message id by design: it never
/// accepts a title, body, or any other note content. Every failure (a failed
/// registration, or an exception from the sink while showing a notification)
/// flips <see cref="NotificationsAvailable"/> to false so the caller keeps
/// working through the pet bubble fallback instead of retrying a broken
/// notification surface.
/// </summary>
public sealed class AppNotificationService : INotificationService, IRegistrableNotificationService
{
    private readonly INotificationSink _sink;
    private volatile bool _notificationsAvailable = true;

    public AppNotificationService(INotificationSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public bool NotificationsAvailable => _notificationsAvailable;

    public async Task<bool> TryRegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            var registered = await _sink.TryRegisterAsync(cancellationToken);
            _notificationsAvailable = registered;
            return registered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _notificationsAvailable = false;
            return false;
        }
    }

    public Task ShowReminderAsync(
        string reminderId,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var request = new NotificationRequest(
            title,
            null,
            null,
            new[]
            {
                new NotificationButton("Done", $"action=reminder-done&reminderId={reminderId}"),
                new NotificationButton("Snooze", $"action=reminder-snooze&reminderId={reminderId}"),
            });
        return ShowIfAvailableAsync(request, cancellationToken);
    }

    public Task ShowRemoteNoteArrivalAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var request = new NotificationRequest(
            "A note arrived 💌",
            null,
            $"action=open-note&messageId={messageId:D}",
            Array.Empty<NotificationButton>());
        return ShowIfAvailableAsync(request, cancellationToken);
    }

    private async Task ShowIfAvailableAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (!_notificationsAvailable)
        {
            return;
        }

        try
        {
            await _sink.ShowAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _notificationsAvailable = false;
        }
    }
}
