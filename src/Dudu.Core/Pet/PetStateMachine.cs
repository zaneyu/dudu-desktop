using Dudu.Core.Models;

namespace Dudu.Core.Pet;

public sealed class PetStateMachine
{
    private readonly HashSet<string> _dueReminderIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remoteMessageIds = new(StringComparer.Ordinal);
    private PetPresentation _current;
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

    public PetPresentation Current => _current;

    public static PetStateMachine CreateIdle()
    {
        return new PetStateMachine(Present(PetState.Idle, "idle"));
    }

    public PetPresentation Handle(PetEvent petEvent)
    {
        ArgumentNullException.ThrowIfNull(petEvent);

        switch (petEvent)
        {
            case PetEvent.ComfortRequested:
                _comfortActive = true;
                break;

            case PetEvent.RemoteNoteArrived remoteNote:
                _remoteMessageIds.Add(remoteNote.MessageId);
                break;

            case PetEvent.ReminderDue reminder:
                _dueReminderIds.Add(reminder.ReminderId);
                break;

            case PetEvent.FocusStarted focus:
                _focusId = focus.FocusId;
                _focusTransition = null;
                _ambientAnimation = null;
                break;

            case PetEvent.FocusEnded focus:
                if (_focusId is null || string.Equals(_focusId, focus.FocusId, StringComparison.Ordinal))
                {
                    _focusId = null;
                    _focusTransition = "focus-end";
                    _ambientAnimation = null;
                }

                break;

            case PetEvent.WelcomeBackRequested:
                _welcomeBackPending = true;
                break;

            case PetEvent.AmbientRequested ambient:
                if (!_paused && !IsFocusActive() && IsAllowedAmbientAnimation(ambient.AnimationKey))
                {
                    _ambientAnimation = ambient.AnimationKey;
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
                _focusTransition = null;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(petEvent), petEvent, "Unknown pet event.");
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

        if (!IsFocusActive() && _remoteMessageIds.Count > 0)
        {
            return new(PetState.RemoteNote, "note-arrival", "A note arrived 💌", null, true);
        }

        if (!IsFocusActive() && _dueReminderIds.Count > 0)
        {
            return Present(PetState.Reminder, "reminder");
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

    private void Dismiss(string itemId)
    {
        if (string.Equals(itemId, "comfort", StringComparison.Ordinal)
            || string.Equals(itemId, "comfort-hug", StringComparison.Ordinal))
        {
            _comfortActive = false;
        }

        if (string.Equals(itemId, "focus-end", StringComparison.Ordinal)
            || string.Equals(itemId, "focus-transition", StringComparison.Ordinal))
        {
            _focusTransition = null;
        }

        if (string.Equals(itemId, "welcome-back", StringComparison.Ordinal)
            || string.Equals(itemId, "greeting", StringComparison.Ordinal))
        {
            _welcomeBackPending = false;
        }

        if (string.Equals(itemId, _ambientAnimation, StringComparison.Ordinal))
        {
            _ambientAnimation = null;
        }

        if (_remoteMessageIds.Remove(itemId))
        {
            return;
        }

        _dueReminderIds.Remove(itemId);
    }

    private bool IsFocusActive() => _focusId is not null;

    private static bool IsAllowedAmbientAnimation(string animationKey)
    {
        return animationKey is "idle" or "blink" or "wave" or "sleep";
    }

    private static PetPresentation Present(PetState state, string animationKey)
    {
        return new(state, animationKey, null, null, false);
    }
}
