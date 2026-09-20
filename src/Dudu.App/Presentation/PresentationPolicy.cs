using Dudu.Core.Models;

namespace Dudu.App.Presentation;

/// <summary>The category of an unsolicited event waiting to be presented.</summary>
public enum PresentationItemKind
{
    RemoteNote,
    Reminder,
    Ambient,
    LocalNote,
}

/// <summary>
/// A privacy-safe description of an unsolicited event. Remote items carry
/// only identifiers; local-note text stays in-process so the pet can render
/// the selected local note without involving notifications or the relay.
/// </summary>
public sealed record DurableNotification
{
    public PresentationItemKind Kind { get; }
    public string Id { get; }
    public string? Title { get; }
    public string? Body { get; }
    public string? AnimationKey { get; }
    public DateTimeOffset? ExpiresUtc { get; }

    internal bool IsExpired(DateTimeOffset nowUtc) => ExpiresUtc is { } expiry && nowUtc >= expiry;

    internal bool IsRoutine => Kind == PresentationItemKind.Reminder
        && Id is Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId
            or Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId;

    /// <summary>
    /// The identity used to detect duplicates, both while an item sits in
    /// <see cref="PresentationPolicy"/>'s queue and while it is currently
    /// being presented (tracked by <c>PresentationCoordinator</c>).
    /// </summary>
    internal string Key => $"{Kind}:{Id}";

    private DurableNotification(
        PresentationItemKind kind,
        string id,
        string? title,
        string? body = null,
        string? animationKey = null,
        DateTimeOffset? expiresUtc = null)
    {
        Kind = kind;
        Id = id;
        Title = title;
        Body = body;
        AnimationKey = animationKey;
        ExpiresUtc = expiresUtc;
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

    public static DurableNotification Reminder(
        string reminderId,
        string title,
        string? body = null,
        string? animationKey = null,
        DateTimeOffset? expiresUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new DurableNotification(PresentationItemKind.Reminder, reminderId, title, body, animationKey, expiresUtc);
    }

    /// <summary>
    /// Ambient items are never queued (see <see cref="PresentationPolicy.Enqueue"/>);
    /// this factory exists so that discard behavior is real, constructible
    /// code rather than an enum value nothing can produce.
    /// </summary>
    public static DurableNotification Ambient(string animationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        return new DurableNotification(
            PresentationItemKind.Ambient,
            animationKey,
            null,
            animationKey: animationKey);
    }

    /// <summary>
    /// Creates a local note selected by the ambient scheduler. It is a
    /// queueable durable presentation item, unlike a bare ambient animation,
    /// so failed playback can be retried without selecting or recording a
    /// second note.
    /// </summary>
    public static DurableNotification LocalNote(
        LocalLoveNote note,
        string animationKey)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        return new DurableNotification(
            PresentationItemKind.LocalNote,
            note.Id,
            "A little note for you",
            note.Text,
            animationKey);
    }
}

/// <summary>The outcome of one <see cref="PresentationPolicy.Decide"/> call.</summary>
/// <param name="PurgedKeys">
/// Keys of items silently dropped from the queue this call because they had
/// expired, so a caller tracking per-key state outside the queue (such as
/// <c>PresentationCoordinator</c>'s toasted-while-held set) can clear it for
/// the same key instead of leaking it onto a future item that happens to
/// recur under the same key.
/// </param>
public sealed record PresentationDecision(
    IReadOnlyList<DurableNotification> ToPresent,
    int RemainingQueuedCount,
    IReadOnlyList<string> PurgedKeys);

/// <summary>
/// The single policy that decides whether an unsolicited event is queued,
/// discarded, or released. It owns the durable queue and the minimum silent
/// interval between releases so a suppressed burst drains one item at a time
/// instead of flooding the user the moment quiet hours, fullscreen, a locked
/// session, or a pause ends.
/// </summary>
/// <remarks>
/// Every public member locks its own internal state (<see cref="_sync"/>),
/// so this type is safe for concurrent callers on its own — it does not rely
/// on a caller (such as <c>AppHost</c>'s reminder-tick semaphore) to
/// serialize access.
/// </remarks>
public sealed class PresentationPolicy
{
    private readonly object _sync = new();
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

    public int QueuedCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

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

        lock (_sync)
        {
            if (!_queuedIds.Add(item.Key))
            {
                return false;
            }

            _queue.Enqueue(item);
            return true;
        }
    }

    /// <summary>Returns an item to the durable queue after a presentation
    /// attempt was cancelled or failed. Requeueing is idempotent and preserves
    /// the acknowledgement boundary owned by the caller.</summary>
    public bool Requeue(DurableNotification item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == PresentationItemKind.Ambient)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_queuedIds.Add(item.Key))
            {
                return false;
            }

            _queue.Enqueue(item);
            return true;
        }
    }

    /// <summary>
    /// True when an item with the same kind+id is currently sitting in the
    /// queue (not yet handed back by <see cref="Decide"/>).
    /// </summary>
    public bool IsQueued(DurableNotification item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            return _queuedIds.Contains(item.Key);
        }
    }

    /// <summary>
    /// Removes a queued item by key, for good, without presenting it.
    /// Used when the event it represents is separately resolved (e.g. a
    /// reminder completed from the Reminders page) before this queue ever
    /// released it. Returns true when an item was actually removed.
    /// </summary>
    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            if (!_queuedIds.Remove(key))
            {
                return false;
            }

            var kept = new Queue<DurableNotification>(_queue.Count);
            while (_queue.Count > 0)
            {
                var candidate = _queue.Dequeue();
                if (candidate.Key != key)
                {
                    kept.Enqueue(candidate);
                }
            }

            while (kept.Count > 0)
            {
                _queue.Enqueue(kept.Dequeue());
            }

            return true;
        }
    }

    /// <summary>
    /// Records that an item was released immediately (not from the queue)
    /// so the minimum silent interval still applies to whatever is released
    /// next from the queue.
    /// </summary>
    public void RecordImmediateRelease(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            _lastReleaseUtc = nowUtc;
        }
    }

    /// <summary>
    /// Releases at most one queued durable item, and only when the
    /// environment is fully clear (not quiet, not fullscreen, not paused,
    /// session not locked, focus not active, pet not hidden by the user) and
    /// the minimum silent interval has elapsed since the last release.
    /// </summary>
    public PresentationDecision Decide(
        bool nowQuiet,
        bool fullscreen,
        bool paused,
        bool sessionLocked = false,
        bool focusActive = false,
        DateTimeOffset? nowUtc = null,
        bool recordRelease = true,
        bool userHidden = false)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var suppressed = nowQuiet || fullscreen || paused || sessionLocked || focusActive || userHidden;
        lock (_sync)
        {
            List<string>? purgedKeys = null;
            var queuedCount = _queue.Count;
            for (var index = 0; index < queuedCount; index++)
            {
                var queued = _queue.Dequeue();
                if (queued.IsExpired(now))
                {
                    _queuedIds.Remove(queued.Key);
                    (purgedKeys ??= new List<string>()).Add(queued.Key);
                }
                else
                {
                    _queue.Enqueue(queued);
                }
            }

            IReadOnlyList<string> purged = purgedKeys ?? (IReadOnlyList<string>)Array.Empty<string>();

            if (_queue.Count == 0
                || suppressed
                || (_lastReleaseUtc is { } last && now - last < _minimumSilentInterval))
            {
                return new PresentationDecision(Array.Empty<DurableNotification>(), _queue.Count, purged);
            }

            var item = _queue.Dequeue();
            _queuedIds.Remove(item.Key);
            if (recordRelease)
            {
                _lastReleaseUtc = now;
            }
            return new PresentationDecision(new[] { item }, _queue.Count, purged);
        }
    }
}
