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

    /// <summary>A user-initiated one-shot (petting, a drink) that plays over
    /// focus and eating without ending them.</summary>
    Interaction,

    /// <summary>The overlay is being dragged by the pointer (drag loop).</summary>
    Dragging,

    /// <summary>An eat-together meal is running (eat loop). Suppresses
    /// unsolicited presentations exactly like focus does.</summary>
    Eating,
}

public sealed record PetPresentation(
    PetState State,
    string AnimationKey,
    string? BubbleTitle,
    string? BubbleBody,
    bool RequiresRecipientAction);
