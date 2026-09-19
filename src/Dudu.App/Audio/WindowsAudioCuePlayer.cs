using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Foundation;

namespace Dudu.App.Audio;

public sealed class WindowsAudioCuePlayer : IAudioCuePlayer, IAudioCuePlayerLifecycle
{
    /// <summary>
    /// Extra time allowed past a cue's own duration before the completion
    /// wait times out -- covers MediaPlayer's own startup/decode latency.
    /// </summary>
    private static readonly TimeSpan CompletionSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Fallback wait when a cue carries no usable duration.
    /// </summary>
    private static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromSeconds(5);

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
            try { player.Source = null; } catch { }
            completion.TrySetResult(AudioPlaybackState.Suppressed);
        });

        // A MediaEnded/MediaFailed that never fires (a hung native decoder)
        // must not await completion.Task forever -- this call sits on the
        // single 30-second reminder tick loop (via AudioCueService.TryPlayAsync),
        // so a stalled MediaPlayer would otherwise stop reminders, note
        // delivery, and ambient behaviour for the rest of the session.
        var completionTimeout = cue.DurationMs > 0
            ? TimeSpan.FromMilliseconds(cue.DurationMs) + CompletionSlack
            : DefaultCompletionTimeout;
        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            player.Play();
            return await completion.Task.WaitAsync(completionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return AudioPlaybackState.Failed;
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
            try { player.Source = null; } catch { }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
