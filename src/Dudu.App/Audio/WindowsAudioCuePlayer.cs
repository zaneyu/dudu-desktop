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

        // Not a `using`: on the TimeoutException path below, teardown is
        // deliberately deferred to a background Task.Run instead of running
        // here, so `player` must survive past this method returning.
        var player = new MediaPlayer
        {
            Volume = Math.Clamp(volume, 0.0, 1.0),
            // A cue is a short sound effect, not media: without these every
            // chirp registered as a media session, popped the Windows media
            // flyout next to the volume overlay, and could capture the
            // keyboard play/pause keys away from her music player.
            AudioCategory = MediaPlayerAudioCategory.SoundEffects,
        };
        player.CommandManager.IsEnabled = false;
        var completion = new TaskCompletionSource<AudioPlaybackState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) =>
            completion.TrySetResult(AudioPlaybackState.Completed);
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, _) =>
            completion.TrySetResult(AudioPlaybackState.Failed);
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        // Finding H: this callback must only ever complete the TCS. The
        // `using var cancellation` below disposes the registration on every
        // exit path, and CancellationTokenRegistration.Dispose() blocks
        // until a currently-running callback returns -- if that callback
        // touched `player` (Source = null, as it used to) while a hung
        // native decoder still owned it (see the TimeoutException path
        // below), disposing the registration -- and therefore this whole
        // method -- could still stall despite the completion timeout that
        // exists specifically to prevent that. All player teardown stays in
        // `finally`, either inline or deferred to its own background task.
        using var cancellation = cancellationToken.Register(() =>
        {
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
        var timedOut = false;
        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            player.Play();
            return await completion.Task.WaitAsync(completionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
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
            if (timedOut)
            {
                // The timeout above means the decoder never called back --
                // it may be hung, and `Source = null`/Dispose() can block on
                // that same native call. Tearing it down inline here would
                // reintroduce the exact stall the timeout exists to avoid
                // (this call sits on the 30-second reminder tick loop), so
                // it runs on its own background task instead, independent of
                // this method's caller. The event unsubscribes are exactly
                // as likely to block against a hung native decoder as
                // Source/Dispose are -- Finding 15: they used to still run
                // inline here even on this branch, so they moved into the
                // same deferred task instead of only the teardown calls.
                var hungPlayer = player;
                _ = Task.Run(() =>
                {
                    try { hungPlayer.MediaEnded -= ended; } catch { }
                    try { hungPlayer.MediaFailed -= failed; } catch { }
                    try { hungPlayer.Source = null; } catch { }
                    try { hungPlayer.Dispose(); } catch { }
                });
            }
            else
            {
                player.MediaEnded -= ended;
                player.MediaFailed -= failed;
                try { player.Source = null; } catch { }
                // Finding H: guard this the same way the deferred hung-player
                // teardown above already does -- Dispose() must never throw
                // out of this finally block.
                try { player.Dispose(); } catch { }
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
