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
}
