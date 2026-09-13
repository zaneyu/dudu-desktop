namespace Dudu.App.Presentation;

/// <summary>The category of an unsolicited event waiting to be presented.</summary>
public enum PresentationItemKind
{
    RemoteNote,
    Reminder,
    Ambient,
}

/// <summary>
/// A privacy-safe description of an unsolicited event. It never carries note
/// content — only the identifiers needed to raise the matching pet event and
/// notification.
/// </summary>
public sealed record DurableNotification
{
    public PresentationItemKind Kind { get; }
    public string Id { get; }
    public string? Title { get; }

    private DurableNotification(PresentationItemKind kind, string id, string? title)
    {
        Kind = kind;
        Id = id;
        Title = title;
    }

    /// <summary>
    /// Messages are addressed only by a protocol-safe message id (a GUID in
    /// "D" format). A non-GUID id is rejected without echoing the offending
    /// value back in the exception message.
    /// </summary>
    public static DurableNotification RemoteNote(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (!Guid.TryParseExact(messageId, "D", out _))
        {
            throw new ArgumentException(
                "The remote note message id is not a protocol-safe GUID.",
                nameof(messageId));
        }

        return new DurableNotification(PresentationItemKind.RemoteNote, messageId, null);
    }

    public static DurableNotification Reminder(string reminderId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new DurableNotification(PresentationItemKind.Reminder, reminderId, title);
    }

    /// <summary>
    /// Ambient items are never queued (see <see cref="PresentationPolicy.Enqueue"/>);
    /// this factory exists so that discard behavior is real, constructible
    /// code rather than an enum value nothing can produce.
    /// </summary>
    public static DurableNotification Ambient(string animationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        return new DurableNotification(PresentationItemKind.Ambient, animationKey, null);
    }
}

/// <summary>The outcome of one <see cref="PresentationPolicy.Decide"/> call.</summary>
public sealed record PresentationDecision(
    IReadOnlyList<DurableNotification> ToPresent,
    int RemainingQueuedCount);

/// <summary>
/// The single policy that decides whether an unsolicited event is queued,
/// discarded, or released. It owns the durable queue and the minimum silent
/// interval between releases so a suppressed burst drains one item at a time
/// instead of flooding the user the moment quiet hours, fullscreen, a locked
/// session, or a pause ends.
/// </summary>
public sealed class PresentationPolicy
{
    private readonly TimeSpan _minimumSilentInterval;
    private readonly Queue<DurableNotification> _queue = new();
    private readonly HashSet<string> _queuedIds = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastReleaseUtc;

    public PresentationPolicy(TimeSpan minimumSilentInterval)
    {
        if (minimumSilentInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSilentInterval));
        }

        _minimumSilentInterval = minimumSilentInterval;
    }

    public int QueuedCount => _queue.Count;

    /// <summary>
    /// Queues a durable item. Ambient items are always discarded rather than
    /// queued, and an item whose kind+id is already queued is not
    /// double-queued. Returns true when the item was actually enqueued.
    /// </summary>
    public bool Enqueue(DurableNotification item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == PresentationItemKind.Ambient)
        {
            return false;
        }

        if (!_queuedIds.Add(Key(item)))
        {
            return false;
        }

        _queue.Enqueue(item);
        return true;
    }

    /// <summary>
    /// Records that an item was released immediately (not from the queue)
    /// so the minimum silent interval still applies to whatever is released
    /// next from the queue.
    /// </summary>
    public void RecordImmediateRelease(DateTimeOffset nowUtc) => _lastReleaseUtc = nowUtc;

    /// <summary>
    /// Releases at most one queued durable item, and only when the
    /// environment is fully clear (not quiet, not fullscreen, not paused,
    /// session not locked, focus not active) and the minimum silent interval
    /// has elapsed since the last release.
    /// </summary>
    public PresentationDecision Decide(
        bool nowQuiet,
        bool fullscreen,
        bool paused,
        bool sessionLocked = false,
        bool focusActive = false,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var suppressed = nowQuiet || fullscreen || paused || sessionLocked || focusActive;
        if (_queue.Count == 0
            || suppressed
            || (_lastReleaseUtc is { } last && now - last < _minimumSilentInterval))
        {
            return new PresentationDecision(Array.Empty<DurableNotification>(), _queue.Count);
        }

        var item = _queue.Dequeue();
        _queuedIds.Remove(Key(item));
        _lastReleaseUtc = now;
        return new PresentationDecision(new[] { item }, _queue.Count);
    }

    private static string Key(DurableNotification item) => $"{item.Kind}:{item.Id}";
}
