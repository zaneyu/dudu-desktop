using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    public const string ReminderGroup = "reminders";

    /// <summary>A reminder toast left unanswered is stale after this long;
    /// the next occurrence raises a fresh one.</summary>
    public static readonly TimeSpan ReminderToastLifetime = TimeSpan.FromHours(4);

    private const int MaxTagLength = 64;

    private readonly INotificationSink _sink;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private volatile bool _notificationsAvailable = true;
    private bool _registered;

    public AppNotificationService(INotificationSink sink, Func<DateTimeOffset>? utcNow = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool NotificationsAvailable => _notificationsAvailable;

    /// <summary>Registers once; later callers (presentation start, the
    /// safe-mode notice) reuse a successful registration instead of calling
    /// the platform Register again.</summary>
    public async Task<bool> TryRegisterAsync(CancellationToken cancellationToken)
    {
        await _registrationGate.WaitAsync(cancellationToken);
        try
        {
            if (_registered)
            {
                return true;
            }

            var registered = await _sink.TryRegisterAsync(cancellationToken);
            _registered = registered;
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
        finally
        {
            _registrationGate.Release();
        }
    }

    /// <summary>Tells the user this run started in crash-loop safe mode.
    /// Display-safe static text only.</summary>
    public Task ShowSafeModeNoticeAsync(CancellationToken cancellationToken) =>
        ShowIfAvailableAsync(
            new NotificationRequest(
                "Dudu started in safe mode",
                "Dudu closed unexpectedly several times, so the desktop pet and remote notes are paused for this run. They return after a normal restart.",
                null,
                Array.Empty<NotificationButton>()),
            cancellationToken);

    public Task ShowReminderAsync(
        string reminderId,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var id = NotificationArguments.Pair("reminderId", reminderId);
        var request = new NotificationRequest(
            title,
            null,
            null,
            new[]
            {
                new NotificationButton("Done", "action=reminder-done&" + id),
                new NotificationButton("Snooze", "action=reminder-snooze&" + id),
            },
            Tag: ReminderTag(reminderId),
            Group: ReminderGroup,
            ExpirationTime: _utcNow() + ReminderToastLifetime);
        return ShowIfAvailableAsync(request, cancellationToken);
    }

    /// <summary>Removes a reminder's toast once the user acknowledged it (in
    /// the app or from the toast) so a stale Done/Snooze copy does not linger
    /// in Action Center. Best-effort: a failure never fails the acknowledgement.</summary>
    public async Task DismissReminderAsync(string reminderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        if (!_notificationsAvailable)
        {
            return;
        }

        try
        {
            await _sink.RemoveAsync(ReminderTag(reminderId), ReminderGroup, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Dudu reminder toast removal failed: {0}", exception.Message);
        }
    }

    /// <summary>Toast tags are limited to 64 characters; longer ids map to a
    /// stable digest so show and remove always agree.</summary>
    internal static string ReminderTag(string reminderId) =>
        reminderId.Length <= MaxTagLength
            ? reminderId
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reminderId)));

    public Task ShowRemoteNoteArrivalAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var request = new NotificationRequest(
            "A note arrived 💌",
            null,
            "action=open-note&" + NotificationArguments.Pair("messageId", messageId.ToString("D")),
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
