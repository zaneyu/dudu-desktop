namespace Dudu.Core.Models;

public enum PetState
{
    Comfort,
    RemoteNote,
    WelcomeBack,
    Ambient,
    Idle,

    /// <summary>A user-initiated one-shot (petting, a drink) that plays over
    /// eating without ending it.</summary>
    Interaction,

    /// <summary>The overlay is being dragged by the pointer (drag loop).</summary>
    Dragging,

    /// <summary>An eat-together meal is running (eat loop). Suppresses
    /// unsolicited presentations until it ends.</summary>
    Eating,

    /// <summary>A study-together session is running (focus loop). Suppresses
    /// unsolicited presentations until it ends, like a meal.</summary>
    Studying,

    /// <summary>Dudu is singing his song (sing loop) for its length or until
    /// stopped. Suppresses unsolicited presentations like a meal.</summary>
    Singing,
}

public sealed record PetPresentation(
    PetState State,
    string AnimationKey,
    string? BubbleTitle,
    string? BubbleBody,
    bool RequiresRecipientAction);
