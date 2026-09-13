namespace Dudu.Core.Abstractions;

/// <summary>
/// Presents notifications to the user without ever carrying note content.
/// Implementations live in the application layer so Dudu.Core stays
/// independent of any specific notification platform (Windows App
/// notifications, the pet bubble fallback, or a test double).
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Shows a reminder notification. The reminder's own title is safe to
    /// display in full; it is never note content.
    /// </summary>
    Task ShowReminderAsync(
        string reminderId,
        string title,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shows a generic "a note arrived" notification. This method accepts no
    /// content parameter by design: a remote note's title or body must never
    /// reach a notification surface.
    /// </summary>
    Task ShowRemoteNoteArrivalAsync(
        Guid messageId,
        CancellationToken cancellationToken);
}
