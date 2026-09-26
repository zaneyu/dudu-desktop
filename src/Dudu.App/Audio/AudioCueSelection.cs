using Dudu.App.Presentation;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Audio;

public enum AudioCueEvent
{
    Greeting,
    RemoteNote,
    Reminder,
    Celebration,
    ManualInteraction,
    Petted,
    Drink,
    Eat,
    Tantrum,
    Drag,
    Sticker,
}

public static class AudioCueSelection
{
    // Every event reuses the five reviewed packs (AudioManifestContract
    // requires exactly those); each maps to two or three so repeats rotate.
    private static readonly IReadOnlyDictionary<AudioCueEvent, IReadOnlyList<string>> PackMap =
        new Dictionary<AudioCueEvent, IReadOnlyList<string>>
        {
            [AudioCueEvent.Greeting] = ["tata-lala", "dudu-lalala"],
            [AudioCueEvent.RemoteNote] = ["bubu-dudu-atata", "dudu-atatata"],
            [AudioCueEvent.Reminder] = ["dudu-yapapa", "tata-lala"],
            [AudioCueEvent.Celebration] = ["dudu-atatata", "bubu-dudu-atata"],
            [AudioCueEvent.ManualInteraction] = ["dudu-lalala", "dudu-yapapa"],
            [AudioCueEvent.Petted] = ["dudu-lalala", "tata-lala"],
            [AudioCueEvent.Drink] = ["dudu-yapapa", "dudu-lalala"],
            [AudioCueEvent.Eat] = ["bubu-dudu-atata", "dudu-yapapa"],
            [AudioCueEvent.Tantrum] = ["dudu-atatata", "bubu-dudu-atata"],
            [AudioCueEvent.Drag] = ["tata-lala", "dudu-yapapa"],
            [AudioCueEvent.Sticker] = ["dudu-lalala", "dudu-yapapa", "tata-lala"],
        };

    public static IReadOnlyList<string> PacksFor(AudioCueEvent cueEvent) =>
        PackMap.TryGetValue(cueEvent, out var packs) ? packs : Array.Empty<string>();

    public static AudioCueEvent? ForPresentation(PetPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return presentation.State switch
        {
            PetState.WelcomeBack => AudioCueEvent.Greeting,
            PetState.FocusTransition => AudioCueEvent.Celebration,
            PetState.RemoteNote => AudioCueEvent.RemoteNote,
            PetState.Reminder => AudioCueEvent.Reminder,
            PetState.Comfort => AudioCueEvent.ManualInteraction,
            // Keyed on the animation key, not a PetState, so interaction
            // states added later (drag, pet, tantrum, eat-together) are
            // covered by whatever state carries these clips.
            _ => presentation.AnimationKey switch
            {
                "greeting" or "welcome-back" => AudioCueEvent.Greeting,
                "celebrate" => AudioCueEvent.Celebration,
                "note-arrival" => AudioCueEvent.RemoteNote,
                "comfort-hug" => AudioCueEvent.ManualInteraction,
                "petted" => AudioCueEvent.Petted,
                "drink" => AudioCueEvent.Drink,
                "eat" => AudioCueEvent.Eat,
                "tantrum" => AudioCueEvent.Tantrum,
                "drag" => AudioCueEvent.Drag,
                var key when Dudu.Core.Assets.AssetManifestContract.IsStickerAnimationKey(key) => AudioCueEvent.Sticker,
                _ => null,
            },
        };
    }

    /// <summary>
    /// Whether a cue for <paramref name="presentation"/> answers a direct user
    /// action and so bypasses the background cooldowns. Ambient and Comfort
    /// presentations only reach the pet paths through an explicit request
    /// (overlay menu, Home, Love Notes reactions -- the ambient scheduler
    /// goes through <see cref="ForNotification"/> instead), and the
    /// interaction clips are user-driven whatever state carries them.
    /// </summary>
    public static AudioCuePriority PriorityFor(PetPresentation presentation, AudioCueEvent cueEvent)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return cueEvent is AudioCueEvent.Petted or AudioCueEvent.Drink or AudioCueEvent.Eat
                or AudioCueEvent.Tantrum or AudioCueEvent.Drag
            || presentation.State is PetState.Ambient or PetState.Comfort
            ? AudioCuePriority.Interactive
            : AudioCuePriority.Background;
    }

    /// <summary>
    /// Events that only settle or clear state (dismissals, acknowledgements,
    /// pause/resume, the end of an interaction, drag or meal) must not
    /// sound: the presentation they return is
    /// whatever was already pending underneath, e.g. dismissing one of two
    /// notes would otherwise replay the note-arrival cue.
    /// </summary>
    public static bool IsSettlingEvent(PetEvent petEvent) => petEvent is
        PetEvent.Dismissed or PetEvent.AmbientDismissed or PetEvent.WelcomeBackDismissed
        or PetEvent.ComfortDismissed or PetEvent.PresentationAcknowledged
        or PetEvent.PauseRequested or PetEvent.ResumeRequested
        or PetEvent.InteractionDismissed or PetEvent.DragEnded or PetEvent.EatingEnded;

    public static AudioCueEvent? ForNotification(DurableNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return notification.Kind switch
        {
            PresentationItemKind.RemoteNote => AudioCueEvent.RemoteNote,
            PresentationItemKind.Reminder => AudioCueEvent.Reminder,
            PresentationItemKind.LocalNote when notification.AnimationKey is { } key
                && Dudu.Core.Assets.AssetManifestContract.IsStickerAnimationKey(key) => AudioCueEvent.Sticker,
            PresentationItemKind.LocalNote => AudioCueEvent.ManualInteraction,
            _ => null,
        };
    }
}
