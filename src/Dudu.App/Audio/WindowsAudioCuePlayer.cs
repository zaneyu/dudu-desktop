using System.Runtime.InteropServices;

namespace Dudu.App.Audio;

/// <summary>
/// Plays a cue with winmm <c>PlaySound(SND_MEMORY | SND_ASYNC | SND_NODEFAULT)</c>
/// from the hash-verified, volume-scaled in-memory WAV bytes.
/// </summary>
/// <remarks>
/// This replaced a <c>Windows.Media.Playback.MediaPlayer</c> implementation
/// that built a <c>file:///</c> URI into the MSIX install directory, created
/// the player on a thread-pool thread, and awaited <c>MediaEnded</c>.
/// PlaySound is the smaller dependency for short PCM effects: it needs no
/// Media Foundation pipeline (absent on N editions), no media session (no
/// media flyout, no captured play/pause keys), no packaged-path URI
/// resolution, and no end-of-media callback -- the one path that could hang.
/// A new PlaySound call stops the previous sound, which is exactly the
/// "one Dudu vocal at a time, a newer cue supersedes" policy.
/// PlaySound has no volume parameter, so the samples are pre-scaled
/// (<see cref="WavPcm.CreateScaledCopy"/>). With SND_ASYNC | SND_MEMORY
/// winmm keeps reading the buffer after the call returns, so the buffer is
/// pinned and kept referenced in <see cref="_playing"/> until a later call
/// or <see cref="DisposeAsync"/> has stopped it.
/// </remarks>
public sealed partial class WindowsAudioCuePlayer : IAudioCuePlayer, IAudioCuePlayerLifecycle
{
    internal const uint SndAsync = 0x0001;
    internal const uint SndNoDefault = 0x0002;
    internal const uint SndMemory = 0x0004;
    internal const uint PlayFlags = SndAsync | SndMemory | SndNoDefault;

    /// <summary>
    /// Bound on a single native start/stop call. PlaySound returns as soon as
    /// the sound has started, but it opens the wave device on first use; a
    /// wedged audio driver must never stall the caller (the 30-second
    /// reminder tick or an explicit pet action) behind it.
    /// </summary>
    internal static readonly TimeSpan NativeCallTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<nint, uint, bool> _playSound;
    private readonly bool _requiresWindows;
    private readonly object _gate = new();
    private byte[]? _playing;
    private int _nativeCallInFlight;
    private int _disposed;

    public WindowsAudioCuePlayer()
        : this(null)
    {
    }

    /// <param name="playSound">Test seam for the native call:
    /// (pointer to WAV bytes or 0 to stop, flags) -> started.</param>
    internal WindowsAudioCuePlayer(Func<nint, uint, bool>? playSound)
    {
        _requiresWindows = playSound is null;
        _playSound = playSound ?? ((sound, flags) => PlaySoundNative(sound, 0, flags));
    }

    public async Task<AudioPlaybackState> PlayAsync(
        AudioCue cue,
        double volume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
            return AudioPlaybackState.Suppressed;
        if (_requiresWindows && !OperatingSystem.IsWindows())
            return AudioPlaybackState.Suppressed;

        var buffer = WavPcm.CreateScaledCopy(cue.WaveData.Span, volume);
        if (buffer is null)
            return AudioPlaybackState.Failed;

        // One native call at a time. If an earlier call is still stuck in the
        // driver, fail fast instead of queueing another blocked thread behind it.
        if (Interlocked.CompareExchange(ref _nativeCallInFlight, 1, 0) != 0)
            return AudioPlaybackState.Failed;

        var call = Task.Run(() =>
        {
            try
            {
                var started = _playSound(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), PlayFlags);
                if (started)
                {
                    // This call already stopped the previous sound, so the
                    // previous buffer may be released now.
                    lock (_gate) _playing = buffer;
                }

                return started;
            }
            finally
            {
                Volatile.Write(ref _nativeCallInFlight, 0);
            }
        });

        try
        {
            var started = await call.WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
            return started ? AudioPlaybackState.Started : AudioPlaybackState.Failed;
        }
        catch (TimeoutException)
        {
            // The closure above keeps `buffer` alive until the stuck call
            // returns, and it is then parked in _playing like any other.
            return AudioPlaybackState.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AudioPlaybackState.Suppressed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            if (_playing is null)
                return;
        }

        // A stuck start call still owns winmm; leave its buffer referenced
        // rather than freeing memory the driver may still read.
        if (Interlocked.CompareExchange(ref _nativeCallInFlight, 1, 0) != 0)
            return;

        var stop = Task.Run(() =>
        {
            try
            {
                if (_playSound(0, 0))
                {
                    lock (_gate) _playing = null;
                }
            }
            finally
            {
                Volatile.Write(ref _nativeCallInFlight, 0);
            }
        });

        try
        {
            await stop.WaitAsync(NativeCallTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown is best-effort: a stop that fails or times out keeps
            // the buffer referenced by the task above until it returns.
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundNative(nint sound, nint module, uint flags);
}
