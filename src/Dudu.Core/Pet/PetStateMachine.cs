using Dudu.Core.Models;

namespace Dudu.Core.Pet;

public sealed class PetStateMachine
{
    /// <summary>
    /// Upper bound for queued reminder/note ids. The pending sets are only used
    /// for a single coalesced display card, so anything beyond the newest N is
    /// dropped (oldest first) instead of growing without bound across a session.
    /// </summary>
    public const int MaxPendingItems = 50;

    private readonly object _sync = new();
    private readonly HashSet<string> _dueReminderIds = new(StringComparer.Ordinal);
    private readonly List<string> _dueReminderOrder = [];
    private readonly HashSet<string> _remoteMessageIds = new(StringComparer.Ordinal);
    private readonly List<string> _remoteMessageOrder = [];
    private PetPresentation _current;

    // Comfort has no auto-timeout by design: a hug must not be yanked mid-display
    // by a timer. It persists until ComfortDismissed, Dismissed("comfort"), or an
    // acknowledgement while the comfort card is showing.
    private bool _comfortActive;
    private string? _focusId;
    private string? _focusTransition;
    private bool _welcomeBackPending;
    private string? _ambientAnimation;
    private bool _paused;

    private PetStateMachine(PetPresentation initial)
    {
        _current = initial;
    }

    public PetPresentation Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Number of pending reminder/note ids backing the coalesced display cards.
    /// Exposed for diagnostics and tests; the display itself stays a single card.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _dueReminderIds.Count + _remoteMessageIds.Count;
            }
        }
    }

    public static PetStateMachine CreateIdle()
    {
        return new PetStateMachine(Present(PetState.Idle, "idle"));
    }

    public PetPresentation Handle(PetEvent petEvent)
    {
        ArgumentNullException.ThrowIfNull(petEvent);

        lock (_sync)
        {
            return HandleCore(petEvent);
        }
    }

    private PetPresentation HandleCore(PetEvent petEvent)
    {

        switch (petEvent)
        {
            case PetEvent.ComfortRequested:
                _comfortActive = true;
                break;

            case PetEvent.ComfortDismissed:
                _comfortActive = false;
                break;

            case PetEvent.RemoteNoteArrived remoteNote:
                AddPending(_remoteMessageIds, _remoteMessageOrder, remoteNote.MessageId);
                break;

            case PetEvent.ReminderDue reminder:
                AddPending(_dueReminderIds, _dueReminderOrder, reminder.ReminderId);
                break;

            case PetEvent.FocusStarted focus:
                _focusId = focus.FocusId;
                _focusTransition = null;
                _ambientAnimation = null;
                break;

            case PetEvent.FocusEnded focus:
                if (_focusId is not null
                    && string.Equals(_focusId, focus.FocusId, StringComparison.Ordinal))
                {
                    _focusId = null;
                    _focusTransition = "focus-end";
                    _ambientAnimation = null;
                }

                break;

            case PetEvent.WelcomeBackRequested:
                _welcomeBackPending = true;
                break;

            case PetEvent.WelcomeBackDismissed:
                _welcomeBackPending = false;
                break;

            case PetEvent.AmbientRequested ambient:
                if (!_paused && !IsFocusActive() && IsAllowedAmbientAnimation(ambient.AnimationKey))
                {
                    _ambientAnimation = ambient.AnimationKey;
                }

                break;

            case PetEvent.AmbientDismissed ambient:
                if (string.Equals(_ambientAnimation, ambient.AnimationKey, StringComparison.Ordinal))
                {
                    _ambientAnimation = null;
                }

                break;

            case PetEvent.Dismissed dismissed:
                Dismiss(dismissed.ItemId);
                break;

            case PetEvent.PauseRequested:
                _paused = true;
                _welcomeBackPending = false;
                _ambientAnimation = null;
                break;

            case PetEvent.ResumeRequested:
                _paused = false;
                break;

            case PetEvent.PresentationAcknowledged:
                if (_current.State == PetState.FocusTransition)
                {
                    _focusTransition = null;
                }

                if (_current.State == PetState.Comfort)
                {
                    _comfortActive = false;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(petEvent), petEvent, "oh no unknown pet event");
        }

        _current = Select();
        return _current;
    }

    private PetPresentation Select()
    {
        if (_comfortActive)
        {
            return Present(PetState.Comfort, "comfort-hug");
        }

        // Pending notes and reminders each coalesce into a single display card
        // no matter how many ids are queued behind it.
        if (!IsFocusActive() && _remoteMessageIds.Count > 0)
        {
            var body = _remoteMessageIds.Count > 1
                ? $"{_remoteMessageIds.Count} notes waiting"
                : null;
            return new(PetState.RemoteNote, "note-arrival", "A note arrived 💌", body, true);
        }

        if (!IsFocusActive() && _dueReminderIds.Count > 0)
        {
            var body = _dueReminderIds.Count > 1
                ? $"{_dueReminderIds.Count} reminders due"
                : null;
            var presentation = Present(PetState.Reminder, "reminder");
            return body is null ? presentation : presentation with { BubbleBody = body };
        }

        if (_focusTransition is not null)
        {
            return Present(PetState.FocusTransition, _focusTransition);
        }

        if (!_paused && _welcomeBackPending)
        {
            return Present(PetState.WelcomeBack, "greeting");
        }

        if (!_paused && !IsFocusActive() && _ambientAnimation is not null)
        {
            return Present(PetState.Ambient, _ambientAnimation);
        }

        if (IsFocusActive())
        {
            return Present(PetState.Focus, "focus");
        }

        return Present(PetState.Idle, "idle");
    }

    private void AddPending(HashSet<string> ids, List<string> order, string id)
    {
        if (!ids.Add(id))
        {
            return;
        }

        order.Add(id);
        while (ids.Count > MaxPendingItems)
        {
            var oldest = order[0];
            order.RemoveAt(0);
            ids.Remove(oldest);
        }
    }

    private void Dismiss(string itemId)
    {
        if (_remoteMessageIds.Remove(itemId))
        {
            _remoteMessageOrder.Remove(itemId);
            return;
        }

        if (_dueReminderIds.Remove(itemId))
        {
            _dueReminderOrder.Remove(itemId);
            return;
        }

        if (string.Equals(itemId, "comfort", StringComparison.Ordinal)
            || string.Equals(itemId, "comfort-hug", StringComparison.Ordinal))
        {
            _comfortActive = false;

            return;
        }

        if (string.Equals(itemId, "focus-end", StringComparison.Ordinal)
            || string.Equals(itemId, "focus-transition", StringComparison.Ordinal))
        {
            _focusTransition = null;

            return;
        }

        if (string.Equals(itemId, "welcome-back", StringComparison.Ordinal))
        {
            _welcomeBackPending = false;

            return;
        }

        if (string.Equals(itemId, _ambientAnimation, StringComparison.Ordinal))
        {
            _ambientAnimation = null;
        }
    }

    private bool IsFocusActive() => _focusId is not null;

    private static bool IsAllowedAmbientAnimation(string animationKey)
    {
        return animationKey is "idle" or "blink" or "wave" or "sleep"
            or "greeting" or "drink" or "celebrate";
    }

    private static PetPresentation Present(PetState state, string animationKey)
    {
        return new(state, animationKey, null, null, false);
    }
}
