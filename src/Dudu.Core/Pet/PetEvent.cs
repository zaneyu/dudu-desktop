namespace Dudu.Core.Pet;

public abstract record PetEvent
{
    public sealed record ComfortRequested : PetEvent;

    public sealed record RemoteNoteArrived : PetEvent
    {
        public string MessageId { get; }

        public RemoteNoteArrived(string messageId)
        {
            MessageId = RequireId(messageId, nameof(messageId));
        }
    }

    public sealed record ReminderDue : PetEvent
    {
        public string ReminderId { get; }

        public ReminderDue(string reminderId)
        {
            ReminderId = RequireId(reminderId, nameof(reminderId));
        }
    }

    public sealed record FocusStarted : PetEvent
    {
        public string FocusId { get; }

        public FocusStarted(string focusId)
        {
            FocusId = RequireId(focusId, nameof(focusId));
        }
    }

    public sealed record FocusEnded : PetEvent
    {
        public string FocusId { get; }

        public FocusEnded(string focusId)
        {
            FocusId = RequireId(focusId, nameof(focusId));
        }
    }

    public sealed record WelcomeBackRequested : PetEvent;

    public sealed record AmbientRequested : PetEvent
    {
        public string AnimationKey { get; }

        public AmbientRequested(string animationKey)
        {
            AnimationKey = RequireAnimation(animationKey);
        }

        private static string RequireAnimation(string animationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
            return animationKey;
        }
    }

    public sealed record Dismissed : PetEvent
    {
        public string ItemId { get; }

        public Dismissed(string itemId)
        {
            ItemId = RequireId(itemId, nameof(itemId));
        }
    }

    public sealed record PauseRequested : PetEvent;

    public sealed record ResumeRequested : PetEvent;

    public sealed record PresentationAcknowledged : PetEvent;

    private static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
