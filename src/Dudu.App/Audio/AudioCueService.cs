using Dudu.App.Hosting;
using Dudu.Core.Models;

namespace Dudu.App.Audio;

public sealed class AudioCueService : IAsyncDisposable
{
    public static readonly TimeSpan GlobalCooldown = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan PackCooldown = TimeSpan.FromMilliseconds(5000);

    private readonly AudioCatalog _catalog;
    private readonly IAudioCuePlayer _player;
    private readonly Func<Preferences> _preferences;
    private readonly Func<bool> _isQuietHours;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isFullscreen;
    private readonly Func<bool> _isSessionLocked;
    private readonly Func<bool> _isSafeMode;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPackPlayback = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _variantIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<AudioCueEvent, int> _packIndexes = new();
    private readonly int _seed;
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset? _lastPlayback;
    private bool _playbackReserved;
    private int _disposed;

    public AudioCueService(
        AudioCatalog catalog,
        IAudioCuePlayer player,
        Func<Preferences> preferences,
        Func<bool>? isQuietHours = null,
        Func<bool>? isPaused = null,
        Func<bool>? isFullscreen = null,
        Func<bool>? isSessionLocked = null,
        Func<bool>? isSafeMode = null,
        IAppHostErrorReporter? errorReporter = null,
        Func<DateTimeOffset>? utcNow = null,
        int seed = 0)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _isQuietHours = isQuietHours ?? (() => false);
        _isPaused = isPaused ?? (() => false);
        _isFullscreen = isFullscreen ?? (() => false);
        _isSessionLocked = isSessionLocked ?? (() => false);
        _isSafeMode = isSafeMode ?? (() => false);
        _errorReporter = errorReporter;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _seed = seed;
    }

    public async Task<AudioPlaybackState> TryPlayAsync(
        AudioCueEvent cueEvent,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested || IsSuppressed())
            return AudioPlaybackState.Suppressed;

        string? packId = null;
        using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var playbackToken = playbackCancellation.Token;
        try
        {
            AudioCue cue;
            int cueIndex;
            int packIndex;
            Preferences preferences;
            lock (_gate)
            {
                var now = _utcNow();
                if (_playbackReserved || now - (_lastPlayback ?? DateTimeOffset.MinValue) < GlobalCooldown)
                    return AudioPlaybackState.Suppressed;

                (packId, cue, cueIndex, packIndex) = ReserveCue(cueEvent, now);
                if (packId is null)
                    return AudioPlaybackState.Suppressed;
                preferences = _preferences();
                _playbackReserved = true;
            }

            playbackToken.ThrowIfCancellationRequested();
            var result = await _player.PlayAsync(
                cue,
                Preferences.ClampSoundVolume(preferences.SoundVolume),
                playbackToken).ConfigureAwait(false);
            if (result.Status is AudioPlaybackStatus.Started or AudioPlaybackStatus.Completed)
            {
                lock (_gate)
                {
                    var now = _utcNow();
                    _lastPlayback = now;
                    _lastPackPlayback[packId!] = now;
                    _variantIndexes[packId!] = cueIndex + 1;
                    _packIndexes[cueEvent] = (packIndex + 1) % AudioCueSelection.PacksFor(cueEvent).Count;
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (playbackToken.IsCancellationRequested)
        {
            return AudioPlaybackState.Suppressed;
        }
        catch (Exception exception)
        {
            _errorReporter?.Report("audio-cue-playback", exception);
            return AudioPlaybackState.Failed;
        }
        finally
        {
            if (packId is not null)
            {
                lock (_gate) _playbackReserved = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            if (_player is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_player is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private bool IsSuppressed()
    {
        var preferences = _preferences();
        return !preferences.SoundsEnabled
            || _isQuietHours()
            || _isPaused()
            || _isFullscreen()
            || _isSessionLocked()
            || _isSafeMode();
    }

    private (string? PackId, AudioCue Cue, int CueIndex, int PackIndex) ReserveCue(AudioCueEvent cueEvent, DateTimeOffset now)
    {
        var packs = AudioCueSelection.PacksFor(cueEvent);
        if (packs.Count == 0) return (null, null!, 0, 0);
        var deterministicSeed = unchecked((uint)(_seed * 31L + (int)cueEvent));
        var start = (int)(deterministicSeed % (uint)packs.Count);
        var offset = _packIndexes.TryGetValue(cueEvent, out var previous) ? previous : start;
        for (var attempt = 0; attempt < packs.Count; attempt++)
        {
            var packIndex = (offset + attempt) % packs.Count;
            var packId = packs[packIndex];
            if (_lastPackPlayback.TryGetValue(packId, out var lastPack)
                && now - lastPack < PackCooldown)
                continue;

            var pack = _catalog.Resolve(packId);
            if (pack.Cues.Count == 0) continue;
            var cueSeed = unchecked((uint)(_seed * 31L + (int)cueEvent + attempt));
            var cueIndex = _variantIndexes.TryGetValue(packId, out var previousCue)
                ? previousCue % pack.Cues.Count
                : (int)(cueSeed % (uint)pack.Cues.Count);
            return (packId, pack.Cues[cueIndex], cueIndex, packIndex);
        }

        return (null, null!, 0, 0);
    }
}
