using Dudu.Core.Models;
using Dudu.Core.Assets;

namespace Dudu.Core.Pet;

public sealed class PetStateMachine
{
    /// <summary>
    /// Upper bound for queued reminder/note ids. The pending sets are only used
    /// for a single coalesced display card, so anything beyond the newest N is
    /// dropped (oldest first) instead of growing without bound across a session.
    /// </summary>
    public const int MaxPendingItems = 50;

    /// <summary>Bubble shown while a focus session is running.</summary>
    public const string FocusBubble = "studying with you 📚";

    /// <summary>Bubble shown while an eat-together meal is running.</summary>
    public const string EatingBubble = "eating together 🍜";

    /// <summary>Bubble shown with the drink pose (overlay action or ambient).</summary>
    public const string DrinkBubble = "drink water ah 💧";

    /// <summary>Bubble shown with the neglect tantrum (see <see cref="AffectionTracker"/>).</summary>
    public const string TantrumBubble = "pet me!! 😤";

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
    private string? _interactionAnimation;
    private string? _eatingId;
    private bool _dragging;
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

    /// <summary>True while a focus session is latched, even when a higher
    /// priority presentation (drag, petting, welcome-back) is on screen.</summary>
    public bool IsFocusActive
    {
        get
        {
            lock (_sync)
            {
                return IsFocusLatched();
            }
        }
    }

    /// <summary>True while an eat-together meal is latched.</summary>
    public bool IsEatingActive
    {
        get
        {
            lock (_sync)
            {
                return IsEatingLatched();
            }
        }
    }

    public bool IsDragging
    {
        get
        {
            lock (_sync)
            {
                return _dragging;
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
                if (!_paused && !IsQuietCompanyActive() && IsAllowedAmbientAnimation(ambient.AnimationKey))
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

            case PetEvent.InteractionRequested interaction:
                if (!_paused && IsAllowedInteractionAnimation(interaction.AnimationKey))
                {
                    _interactionAnimation = interaction.AnimationKey;
                }

                break;

            case PetEvent.InteractionDismissed interaction:
                if (string.Equals(_interactionAnimation, interaction.AnimationKey, StringComparison.Ordinal))
                {
                    _interactionAnimation = null;
                }

                break;

            case PetEvent.DragStarted:
                _dragging = true;
                break;

            case PetEvent.DragEnded:
                _dragging = false;
                break;

            case PetEvent.EatingStarted eating:
                _eatingId = eating.SessionId;
                _ambientAnimation = null;
                break;

            case PetEvent.EatingEnded eating:
                if (string.Equals(_eatingId, eating.SessionId, StringComparison.Ordinal))
                {
                    _eatingId = null;
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
                _interactionAnimation = null;
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
        // A drag is a live physical interaction: whatever else is latched
        // stays latched and returns the moment the pointer is released.
        if (_dragging)
        {
            return Present(PetState.Dragging, "drag");
        }

        if (_comfortActive)
        {
            return Present(PetState.Comfort, "comfort-hug");
        }

        // An explicit user one-shot briefly plays over everything below it
        // (a waiting note card, focus, a meal) and then hands back to it.
        if (!_paused && _interactionAnimation is not null)
        {
            return Present(PetState.Interaction, _interactionAnimation, BubbleFor(_interactionAnimation));
        }

        // Pending notes and reminders each coalesce into a single display card
        // no matter how many ids are queued behind it.
        if (!_paused && !IsQuietCompanyActive() && _remoteMessageIds.Count > 0)
        {
            var body = _remoteMessageIds.Count > 1
                ? $"{_remoteMessageIds.Count} notes waiting"
                : null;
            return new(PetState.RemoteNote, "note-arrival", "A note arrived 💌", body, true);
        }

        if (!_paused && !IsQuietCompanyActive() && _dueReminderIds.Count > 0)
        {
            var body = _dueReminderIds.Count > 1
                ? $"{_dueReminderIds.Count} reminders due"
                : null;
            // Reminder has no separate source clip. The note-arrival pose is
            // the closest real Dudu interaction and keeps a due reminder from
            // degrading to the generic idle fallback.
            var presentation = Present(PetState.Reminder, "note-arrival");
            return body is null ? presentation : presentation with { BubbleBody = body };
        }

        if (_focusTransition is not null)
        {
            // The thumbs-up clip is the closest real Dudu expression for a
            // completed focus session.
            return Present(PetState.FocusTransition, "celebrate");
        }

        if (!_paused && _welcomeBackPending)
        {
            return Present(PetState.WelcomeBack, "greeting");
        }

        if (!_paused && !IsQuietCompanyActive() && _ambientAnimation is not null)
        {
            return Present(PetState.Ambient, _ambientAnimation, BubbleFor(_ambientAnimation));
        }

        // A meal is the more recent, shorter commitment, so it shows over a
        // focus session that happens to still be running underneath it.
        if (IsEatingLatched())
        {
            return Present(PetState.Eating, "eat", EatingBubble);
        }

        if (IsFocusLatched())
        {
            return Present(PetState.Focus, "focus", FocusBubble);
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

        if (string.Equals(itemId, _interactionAnimation, StringComparison.Ordinal))
        {
            _interactionAnimation = null;
            return;
        }

        if (_eatingId is not null && string.Equals(itemId, _eatingId, StringComparison.Ordinal))
        {
            _eatingId = null;
            return;
        }

        if (string.Equals(itemId, _ambientAnimation, StringComparison.Ordinal))
        {
            _ambientAnimation = null;
        }
    }

    private bool IsFocusLatched() => _focusId is not null;

    private bool IsEatingLatched() => _eatingId is not null;

    /// <summary>Focus and eating both keep Dudu as quiet company: unsolicited
    /// notes, reminders, and ambient moments wait until they end.</summary>
    private bool IsQuietCompanyActive() => IsFocusLatched() || IsEatingLatched();

    private static bool IsAllowedAmbientAnimation(string animationKey)
    {
        return animationKey is "idle" or "blink" or "greeting" or "sleep"
            or "drink" or "celebrate" or "tantrum"
            || AssetManifestContract.IsStickerAnimationKey(animationKey);
    }

    private static bool IsAllowedInteractionAnimation(string animationKey) =>
        animationKey is "petted" or "drink" or "celebrate" or "greeting";

    private static string? BubbleFor(string animationKey) => animationKey switch
    {
        "drink" => DrinkBubble,
        "tantrum" => TantrumBubble,
        _ => null,
    };

    private static PetPresentation Present(PetState state, string animationKey, string? bubbleTitle = null)
    {
        return new(state, animationKey, bubbleTitle, null, false);
    }
}
