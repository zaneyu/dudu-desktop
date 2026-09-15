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
    IReadOnlyList<NotificationButton> Buttons,
    string? Tag = null,
    string? Group = null,
    DateTimeOffset? ExpirationTime = null);

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

    /// <summary>Removes a previously shown notification (including its Action
    /// Center copy). A no-op for sinks that cannot address shown toasts.</summary>
    Task RemoveAsync(string tag, string group, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
