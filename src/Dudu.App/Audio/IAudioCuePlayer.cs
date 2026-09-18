namespace Dudu.App.Audio;

public enum AudioPlaybackStatus
{
    Started,
    Completed,
    Suppressed,
    Failed,
}

public sealed record AudioPlaybackState(AudioPlaybackStatus Status)
{
    public static AudioPlaybackState Started { get; } = new(AudioPlaybackStatus.Started);
    public static AudioPlaybackState Completed { get; } = new(AudioPlaybackStatus.Completed);
    public static AudioPlaybackState Suppressed { get; } = new(AudioPlaybackStatus.Suppressed);
    public static AudioPlaybackState Failed { get; } = new(AudioPlaybackStatus.Failed);
}

public interface IAudioCuePlayer
{
    Task<AudioPlaybackState> PlayAsync(
        AudioCue cue,
        double volume,
        CancellationToken cancellationToken);
}
