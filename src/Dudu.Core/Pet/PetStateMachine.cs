using Dudu.Core.Models;
using Dudu.Core.Assets;

namespace Dudu.Core.Pet;

public sealed class PetStateMachine
{
    /// <summary>
    /// Upper bound for queued note ids. The pending sets are only used
    /// for a single coalesced display card, so anything beyond the newest N is
    /// dropped (oldest first) instead of growing without bound across a session.
    /// </summary>
    public const int MaxPendingItems = 50;

    /// <summary>Bubble shown while an eat-together meal is running.</summary>
    public const string EatingBubble = "eating together 🍜";

    /// <summary>Bubble shown while a study-together session is running.</summary>
    public const string StudyingBubble = "studying with you 📚";

    /// <summary>Bubble shown while Dudu sings his song.</summary>
    public const string SingingBubble = "singing for u 🎵";

    /// <summary>Bubble shown with the drink pose (overlay action or ambient).</summary>
    public const string DrinkBubble = "drink water ah 💧";

    /// <summary>Bubble shown with the neglect tantrum (see <see cref="AffectionTracker"/>).</summary>
    public const string TantrumBubble = "pet me!! 😤";

    /// <summary>Bubbles for the poke-too-much reactions (see <see cref="AffectionTracker"/>).</summary>
    public const string GrumpyBubble = "hmph 😒";
    public const string PokedTantrumBubble = "stop poking me!! 😤";
    public const string FlailBubble = "i dont care i dont care!!";
    public const string WailBubble = "waaah 😭";

    private readonly object _sync = new();
    private readonly HashSet<string> _remoteMessageIds = new(StringComparer.Ordinal);
    private readonly List<string> _remoteMessageOrder = [];
    private PetPresentation _current;

    // Comfort has no auto-timeout by design: a hug must not be yanked mid-display
    // by a timer. It persists until ComfortDismissed, Dismissed("comfort"), or an
    // acknowledgement while the comfort card is showing.
    private bool _comfortActive;
    private bool _welcomeBackPending;
    private string? _ambientAnimation;
    private string? _interactionAnimation;
    private string? _eatingId;
    private string? _studyId;
    private string? _singId;
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
    /// Number of pending note ids backing the coalesced display card.
    /// Exposed for diagnostics and tests; the display itself stays a single card.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _remoteMessageIds.Count;
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

    /// <summary>True while a study-together session is latched.</summary>
    public bool IsStudyingActive
    {
        get
        {
            lock (_sync)
            {
                return _studyId is not null;
            }
        }
    }

    /// <summary>True while a sing-for-me session is latched.</summary>
    public bool IsSingingActive
    {
        get
        {
            lock (_sync)
            {
                return _singId is not null;
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

            case PetEvent.StudyStarted study:
                _studyId = study.SessionId;
                _ambientAnimation = null;
                break;

            case PetEvent.StudyEnded study:
                if (string.Equals(_studyId, study.SessionId, StringComparison.Ordinal))
                {
                    _studyId = null;
                    _ambientAnimation = null;
                }

                break;

            case PetEvent.SingStarted sing:
                _singId = sing.SessionId;
                _ambientAnimation = null;
                break;

            case PetEvent.SingEnded sing:
                if (string.Equals(_singId, sing.SessionId, StringComparison.Ordinal))
                {
                    _singId = null;
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
        // (a waiting note card, a meal) and then hands back to it.
        if (!_paused && _interactionAnimation is not null)
        {
            return Present(PetState.Interaction, _interactionAnimation, InteractionBubbleFor(_interactionAnimation));
        }

        // Pending notes coalesce into a single display card no matter how
        // many ids are queued behind it.
        if (!_paused && !IsQuietCompanyActive() && _remoteMessageIds.Count > 0)
        {
            var body = _remoteMessageIds.Count > 1
                ? $"{_remoteMessageIds.Count} notes waiting"
                : null;
            return new(PetState.RemoteNote, "note-arrival", "A note arrived 💌", body, true);
        }

        if (!_paused && _welcomeBackPending)
        {
            return Present(PetState.WelcomeBack, "greeting");
        }

        if (!_paused && !IsQuietCompanyActive() && _ambientAnimation is not null)
        {
            return Present(PetState.Ambient, _ambientAnimation, BubbleFor(_ambientAnimation));
        }

        if (IsEatingLatched())
        {
            return Present(PetState.Eating, "eat", EatingBubble);
        }

        if (_studyId is not null)
        {
            return Present(PetState.Studying, "focus", StudyingBubble);
        }

        if (_singId is not null)
        {
            return Present(PetState.Singing, "sing", SingingBubble);
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

        if (string.Equals(itemId, "comfort", StringComparison.Ordinal)
            || string.Equals(itemId, "comfort-hug", StringComparison.Ordinal))
        {
            _comfortActive = false;

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

        if (_studyId is not null && string.Equals(itemId, _studyId, StringComparison.Ordinal))
        {
            _studyId = null;
            return;
        }

        if (_singId is not null && string.Equals(itemId, _singId, StringComparison.Ordinal))
        {
            _singId = null;
            return;
        }

        if (string.Equals(itemId, _ambientAnimation, StringComparison.Ordinal))
        {
            _ambientAnimation = null;
        }
    }

    private bool IsEatingLatched() => _eatingId is not null;

    /// <summary>An eat-together meal, a study-together session or a song keeps
    /// Dudu as quiet company: unsolicited notes and ambient moments wait until
    /// it ends.</summary>
    private bool IsQuietCompanyActive() => IsEatingLatched() || _studyId is not null || _singId is not null;

    private static bool IsAllowedAmbientAnimation(string animationKey)
    {
        return animationKey is "idle" or "blink" or "greeting" or "sleep"
            or "drink" or "celebrate" or "tantrum"
            || AssetManifestContract.IsStickerAnimationKey(animationKey)
            || AssetManifestContract.IsMotionAnimationKey(animationKey);
    }

    private static bool IsAllowedInteractionAnimation(string animationKey) =>
        animationKey is "petted" or "drink" or "celebrate" or "greeting"
            or "grumpy" or "tantrum" or "flail" or "wail";

    /// <summary>A tantrum the user asked for by poking is about the poking,
    /// not the neglect <see cref="TantrumBubble"/>.</summary>
    private static string? InteractionBubbleFor(string animationKey) => animationKey switch
    {
        "grumpy" => GrumpyBubble,
        "tantrum" => PokedTantrumBubble,
        "flail" => FlailBubble,
        "wail" => WailBubble,
        _ => BubbleFor(animationKey),
    };

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
