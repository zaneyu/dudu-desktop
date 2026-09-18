using Dudu.App.Hosting;
using Dudu.Core.Models;

namespace Dudu.App.Audio;

public sealed class AudioCueService
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
    private readonly Dictionary<AudioCueEvent, int> _variantIndexes = new();
    private readonly int _seed;
    private DateTimeOffset? _lastPlayback;
    private bool _playbackReserved;

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
        if (cancellationToken.IsCancellationRequested || IsSuppressed())
            return AudioPlaybackState.Suppressed;

        string? packId = null;
        try
        {
            AudioCue cue;
            Preferences preferences;
            lock (_gate)
            {
                var now = _utcNow();
                if (_playbackReserved || now - (_lastPlayback ?? DateTimeOffset.MinValue) < GlobalCooldown)
                    return AudioPlaybackState.Suppressed;

                (packId, cue) = ReserveCue(cueEvent, now);
                if (packId is null)
                    return AudioPlaybackState.Suppressed;
                preferences = _preferences();
                _playbackReserved = true;
                _lastPlayback = now;
                _lastPackPlayback[packId] = now;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await _player.PlayAsync(
                cue,
                Preferences.ClampSoundVolume(preferences.SoundVolume),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    private (string? PackId, AudioCue Cue) ReserveCue(AudioCueEvent cueEvent, DateTimeOffset now)
    {
        var packs = AudioCueSelection.PacksFor(cueEvent);
        if (packs.Count == 0) return (null, null!);
        var deterministicSeed = unchecked((uint)(_seed * 31L + (int)cueEvent));
        var start = (int)(deterministicSeed % (uint)packs.Count);
        var offset = _variantIndexes.TryGetValue(cueEvent, out var previous) ? previous : start;
        for (var attempt = 0; attempt < packs.Count; attempt++)
        {
            var packId = packs[(offset + attempt) % packs.Count];
            if (_lastPackPlayback.TryGetValue(packId, out var lastPack)
                && now - lastPack < PackCooldown)
                continue;

            var pack = _catalog.Resolve(packId);
            if (pack.Cues.Count == 0) continue;
            _variantIndexes[cueEvent] = (offset + attempt + 1) % packs.Count;
            return (packId, pack.Cues[0]);
        }

        return (null, null!);
    }
}
