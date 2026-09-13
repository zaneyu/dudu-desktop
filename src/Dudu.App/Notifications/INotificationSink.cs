namespace Dudu.App.Notifications;

/// <summary>A notification action button and the activation arguments it carries.</summary>
public sealed record NotificationButton(string Label, string ActivationArguments);

/// <summary>
/// A fully-formed notification request. Every field here is display-safe:
/// <see cref="AppNotificationService"/> is the only place allowed to build
/// one, and it never takes note content as an input.
/// </summary>
public sealed record NotificationRequest(
    string Title,
    string? Body,
    string? ActivationArguments,
    IReadOnlyList<NotificationButton> Buttons);

/// <summary>
/// The narrow seam between <see cref="AppNotificationService"/> and the
/// operating system's notification surface. Kept tiny so it can be faked in
/// tests and swapped for the real Windows App SDK implementation in
/// production.
/// </summary>
public interface INotificationSink
{
    Task<bool> TryRegisterAsync(CancellationToken cancellationToken);

    Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken);
}
