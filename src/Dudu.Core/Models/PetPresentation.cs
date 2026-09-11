namespace Dudu.Core.Models;

public enum PetState
{
    Comfort,
    RemoteNote,
    Reminder,
    FocusTransition,
    WelcomeBack,
    Ambient,
    Focus,
    Idle,
}

public sealed record PetPresentation(
    PetState State,
    string AnimationKey,
    string? BubbleTitle,
    string? BubbleBody,
    bool RequiresRecipientAction);
