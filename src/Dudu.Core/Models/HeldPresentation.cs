namespace Dudu.Core.Models;

/// <summary>
/// A durable presentation item persisted for exactly as long as it sits in
/// <c>PresentationPolicy</c>'s in-memory held queue (suppressed by quiet
/// hours, fullscreen, a locked session, or a pause), so it survives an app
/// quit or crash instead of vanishing silently with that in-memory queue.
/// <see cref="Kind"/> is the item's presentation kind rendered as text (e.g.
/// "Reminder"); this layer treats it as opaque data, since the enum it names
/// belongs to the App layer, which is the only reader of these rows.
/// </summary>
public sealed record HeldPresentation(
    string Key,
    string Kind,
    string Id,
    string? Title,
    string? Body,
    string? AnimationKey,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset QueuedUtc);
