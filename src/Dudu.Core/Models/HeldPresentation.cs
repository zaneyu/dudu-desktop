namespace Dudu.Core.Models;

/// <summary>
/// A durable presentation item persisted for exactly as long as it sits in
/// <c>PresentationPolicy</c>'s in-memory held queue (suppressed by quiet
/// hours, fullscreen, a locked session, or a pause), so it survives an app
/// quit or crash instead of vanishing silently with that in-memory queue.
/// <see cref="Kind"/> is the item's presentation kind rendered as text (e.g.
/// "Reminder"); this layer treats it as opaque data, since the enum it names
/// belongs to the App layer, which is the only reader of these rows.
/// <see cref="Toasted"/> mirrors <c>PresentationCoordinator</c>'s in-memory
/// toasted-while-held marker, so a restart does not show the Windows toast a
/// second time for an item that already toasted once before the quit/crash.
/// </summary>
public sealed record HeldPresentation(
    string Key,
    string Kind,
    string Id,
    string? Title,
    string? Body,
    string? AnimationKey,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset QueuedUtc,
    bool Toasted);
