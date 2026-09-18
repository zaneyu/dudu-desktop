using Dudu.App.Presentation;
using Dudu.Core.Models;

namespace Dudu.App.Audio;

public enum AudioCueEvent
{
    Greeting,
    RemoteNote,
    Reminder,
    Celebration,
    ManualInteraction,
}

public static class AudioCueSelection
{
    private static readonly IReadOnlyDictionary<AudioCueEvent, IReadOnlyList<string>> PackMap =
        new Dictionary<AudioCueEvent, IReadOnlyList<string>>
        {
            [AudioCueEvent.Greeting] = ["tata-lala", "dudu-lalala"],
            [AudioCueEvent.RemoteNote] = ["bubu-dudu-atata", "dudu-atatata"],
            [AudioCueEvent.Reminder] = ["dudu-yapapa", "tata-lala"],
            [AudioCueEvent.Celebration] = ["dudu-atatata", "bubu-dudu-atata"],
            [AudioCueEvent.ManualInteraction] = ["dudu-lalala", "dudu-yapapa"],
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
            _ => presentation.AnimationKey switch
            {
                "greeting" or "welcome-back" => AudioCueEvent.Greeting,
                "celebrate" => AudioCueEvent.Celebration,
                "note-arrival" => AudioCueEvent.RemoteNote,
                "comfort-hug" => AudioCueEvent.ManualInteraction,
                _ => null,
            },
        };
    }

    public static AudioCueEvent? ForNotification(DurableNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return notification.Kind switch
        {
            PresentationItemKind.RemoteNote => AudioCueEvent.RemoteNote,
            PresentationItemKind.Reminder => AudioCueEvent.Reminder,
            PresentationItemKind.LocalNote => AudioCueEvent.ManualInteraction,
            _ => null,
        };
    }
}
