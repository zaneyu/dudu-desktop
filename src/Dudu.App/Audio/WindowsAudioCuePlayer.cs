using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Foundation;

namespace Dudu.App.Audio;

public sealed class WindowsAudioCuePlayer : IAudioCuePlayer
{
    private readonly string? _assetRoot;

    public WindowsAudioCuePlayer(string? assetRoot = null)
    {
        _assetRoot = assetRoot;
    }

    public async Task<AudioPlaybackState> PlayAsync(
        AudioCue cue,
        double volume,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_assetRoot))
            return AudioPlaybackState.Completed;

        var root = Path.GetFullPath(_assetRoot);
        var path = Path.GetFullPath(Path.Combine(root, cue.RelativeFile));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return AudioPlaybackState.Completed;

        using var player = new MediaPlayer { Volume = Math.Clamp(volume, 0.0, 1.0) };
        var completion = new TaskCompletionSource<AudioPlaybackState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) =>
            completion.TrySetResult(AudioPlaybackState.Completed);
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, _) =>
            completion.TrySetResult(AudioPlaybackState.Failed);
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        using var cancellation = cancellationToken.Register(() =>
        {
            try { player.Pause(); } catch { }
            completion.TrySetResult(AudioPlaybackState.Suppressed);
        });

        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            player.Play();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AudioPlaybackState.Suppressed;
        }
        catch
        {
            return AudioPlaybackState.Failed;
        }
        finally
        {
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
            try { player.Pause(); } catch { }
        }
    }
}
