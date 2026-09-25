using Dudu.App.Hosting;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Animation;

/// <summary>
/// Bounds user-triggered presentation time, then restores the state machine's
/// authoritative ambient presentation without awaiting a possible loop.
/// </summary>
public sealed class PetPresentationCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(3);
    private readonly PetStateMachine _pet;
    private readonly Func<PetPresentation, AnimationOptions, CancellationToken, Task> _playAsync;
    private readonly Func<PetPresentation, CancellationToken, Task>? _playAudioAsync;
    private readonly Func<AnimationOptions> _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _maximumDuration;
    private readonly SemaphoreSlim _gate;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly object _ambientSync = new();
    private CancellationTokenSource? _ambientCts;
    private bool _disposed;

    /// <param name="gate">
    /// The lock guarding every mutation of <paramref name="pet"/>'s shared
    /// <see cref="PetStateMachine"/>. Defaults to a private instance; pass an
    /// explicit instance (shared with e.g. <c>PresentationCoordinator</c>)
    /// so an explicit one-shot and an unsolicited background release can
    /// never interleave their mutation of the same state machine.
    /// </param>
    /// <param name="errorReporter">
    /// Optional shared diagnostics sink. Playback, restore, and dismissal
    /// failures are reported here first and only fall back to
    /// <see cref="global::System.Diagnostics.Trace"/> when none is composed,
    /// so owner-thread faults stay user-visible instead of Trace-only.
    /// </param>
    public PetPresentationCoordinator(
        PetStateMachine pet,
        Func<PetPresentation, AnimationOptions, CancellationToken, Task> playAsync,
        Func<AnimationOptions>? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? maximumDuration = null,
        SemaphoreSlim? gate = null,
        Func<PetPresentation, CancellationToken, Task>? playAudioAsync = null,
        IAppHostErrorReporter? errorReporter = null)
    {
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _playAudioAsync = playAudioAsync;
        _options = options ?? (() => AnimationOptions.Default);
        _delayAsync = delayAsync ?? Task.Delay;
        _maximumDuration = maximumDuration ?? DefaultMaximumDuration;
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _gate = gate ?? new SemaphoreSlim(1, 1);
        _errorReporter = errorReporter;
    }

    public async Task PresentOneShotAsync(
        PetEvent petEvent,
        string dismissalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(petEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(dismissalId);
        var completionEvent = PetEvent.CompletionForOneShot(petEvent, dismissalId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Cancel any still-flight ambient restore before the one-shot
            // starts, so a stale ambient can never land after the new
            // presentation and the play order stays deterministic.
            CancelAmbientRestore();
            using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var oneShot = _pet.Handle(petEvent);
                // A synchronous throw (e.g. the composer rejecting a bad
                // frame inline) must take the same fallback path as an async
                // fault below: report and present idle instead of leaving the
                // pet invisible.
                Task playback;
                var playbackFaulted = false;
                try
                {
                    playback = _playAsync(oneShot, _options(), playbackCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportFailure("one-shot-playback", exception);
                    await PlayIdleFallbackAsync(cancellationToken);
                    playbackFaulted = true;
                    playback = Task.CompletedTask;
                }

                if (!playbackFaulted)
                {
                    var timeout = _delayAsync(_maximumDuration, timeoutCancellation.Token);
                    if (await Task.WhenAny(playback, timeout) == playback)
                    {
                        timeoutCancellation.Cancel();
                        try
                        {
                            await playback;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            // A bad frame (e.g. an undecodable PNG surfacing
                            // from the composer) must not leave the pet
                            // invisible: log through the reporter and fall
                            // back to a visible idle present so the first
                            // Present still happens.
                            ReportFailure("one-shot-playback", exception);
                            await PlayIdleFallbackAsync(cancellationToken);
                        }

                        if (_playAudioAsync is not null)
                        {
                            await ObserveAudioAsync(
                                () => _playAudioAsync(oneShot, cancellationToken));
                        }
                    }
                    else
                    {
                        playbackCancellation.Cancel();
                        _ = ObserveCanceledPlaybackAsync(playback);
                        await timeout;
                    }
                }
                else
                {
                    timeoutCancellation.Cancel();
                }
            }
            finally
            {
                _pet.Handle(new PetEvent.PresentationAcknowledged());
                if (completionEvent is PetEvent.Dismissed dismissed
                    && !_pet.IsKnownDismissalId(dismissed.ItemId))
                {
                    // Unknown ids are ignored by the state machine by design;
                    // surface the caller bug instead of letting Comfort or the
                    // focus-end transition sit latched with no trace.
                    // CompletionForOneShot already maps the canonical
                    // one-shots (comfort, focus-end) to known ids, so this
                    // only fires for genuinely stale or mistyped ids.
                    ReportFailure(
                        "one-shot-unknown-dismissal",
                        new InvalidOperationException(
                            $"Dismissal id '{dismissed.ItemId}' names nothing pending; ignoring."));
                }

                _pet.Handle(completionEvent);
                StartAmbientRestore(_pet.Current, _options());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Plays the idle presentation best-effort after a faulted one-shot so a
    /// bad asset can never leave the pet transparent. Faults here are
    /// reported and swallowed: the ambient restore in the caller still runs.
    /// </summary>
    private async Task PlayIdleFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _playAsync(
                PetStateMachine.CreateIdle().Current,
                _options(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure("one-shot-idle-fallback", exception);
        }
    }

    /// <summary>
    /// Starts the ambient restore on a tracked token instead of
    /// <see cref="CancellationToken.None"/>. The token is cancelled by the
    /// next <c>PresentOneShotAsync</c> (or <c>Dispose</c>), which keeps
    /// back-to-back ordering deterministic. Like the old fire-and-forget, the
    /// restore is not linked to the caller's token: it must still land after
    /// a cancelled one-shot so the pet never sticks on a transient pose.
    /// </summary>
    private void StartAmbientRestore(PetPresentation ambient, AnimationOptions options)
    {
        CancellationToken token;
        lock (_ambientSync)
        {
            CancelAmbientRestore_NoLock();
            _ambientCts = new CancellationTokenSource();
            token = _ambientCts.Token;
        }

        _ = RestoreAmbientAsync(ambient, options, token);
    }

    private void CancelAmbientRestore()
    {
        lock (_ambientSync)
        {
            CancelAmbientRestore_NoLock();
        }
    }

    private void CancelAmbientRestore_NoLock()
    {
        var cts = _ambientCts;
        _ambientCts = null;
        try
        {
            cts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private async Task RestoreAmbientAsync(
        PetPresentation ambient,
        AnimationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await _playAsync(ambient, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("ambient-restore", exception);
        }
    }

    private async Task ObserveAudioAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReportFailure("audio-cue-playback", exception);
        }
    }

    private async Task ObserveCanceledPlaybackAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReportFailure("cancelled-one-shot-playback", exception);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try
            {
                _errorReporter.Report(operation, exception);
                return;
            }
            catch
            {
            }
        }

        global::System.Diagnostics.Trace.TraceError(
            "Dudu {0} failed: {1}",
            operation,
            exception.GetType().FullName);
    }

    public void Dispose()
    {
        lock (_ambientSync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelAmbientRestore_NoLock();
        }
    }
}
