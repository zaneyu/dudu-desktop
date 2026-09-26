using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.App.Audio;

namespace Dudu.App.Animation;

/// <summary>
/// Bounds user-triggered presentation time, then restores the state machine's
/// authoritative ambient presentation without awaiting a possible loop.
/// </summary>
public sealed class PetPresentationCoordinator
{
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(3);
    private readonly PetStateMachine _pet;
    private readonly Func<PetPresentation, AnimationOptions, CancellationToken, Task> _playAsync;
    private readonly Func<PetPresentation, CancellationToken, Task>? _playAudioAsync;
    private readonly Func<AnimationOptions> _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _maximumDuration;
    private readonly SemaphoreSlim _gate;

    /// <param name="gate">
    /// The lock guarding every mutation of <paramref name="pet"/>'s shared
    /// <see cref="PetStateMachine"/>. Defaults to a private instance; pass an
    /// explicit instance (shared with e.g. <c>PresentationCoordinator</c>)
    /// so an explicit one-shot and an unsolicited background release can
    /// never interleave their mutation of the same state machine.
    /// </param>
    public PetPresentationCoordinator(
        PetStateMachine pet,
        Func<PetPresentation, AnimationOptions, CancellationToken, Task> playAsync,
        Func<AnimationOptions>? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? maximumDuration = null,
        SemaphoreSlim? gate = null,
        Func<PetPresentation, CancellationToken, Task>? playAudioAsync = null)
    {
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _playAudioAsync = playAudioAsync;
        _options = options ?? (() => AnimationOptions.Default);
        _delayAsync = delayAsync ?? Task.Delay;
        _maximumDuration = maximumDuration ?? DefaultMaximumDuration;
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _gate = gate ?? new SemaphoreSlim(1, 1);
    }

    public Task PresentOneShotAsync(
        PetEvent petEvent,
        string dismissalId,
        CancellationToken cancellationToken = default) =>
        PresentOneShotCoreAsync(petEvent, dismissalId, requireIdle: false, companion: null, cancellationToken);

    /// <summary>
    /// Plays an unsolicited idle one-shot (a fidget or a wander) only if the pet is
    /// still plainly idle once the shared gate is held, so it can never preempt or
    /// replay a note, reminder, comfort, focus, or another one-shot. The optional
    /// <paramref name="companion"/> (the overlay's sideways glide for a wander) starts
    /// together with the clip and is cancelled with it if the clip hits the time cap.
    /// </summary>
    /// <returns>False when the pet was not idle and nothing was played.</returns>
    public Task<bool> TryPresentIdleOneShotAsync(
        PetEvent petEvent,
        string dismissalId,
        Func<CancellationToken, Task>? companion = null,
        CancellationToken cancellationToken = default) =>
        PresentOneShotCoreAsync(petEvent, dismissalId, requireIdle: true, companion, cancellationToken);

    private async Task<bool> PresentOneShotCoreAsync(
        PetEvent petEvent,
        string dismissalId,
        bool requireIdle,
        Func<CancellationToken, Task>? companion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(petEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(dismissalId);
        var completionEvent = PetEvent.CompletionForOneShot(petEvent, dismissalId);
        // Captured under the gate (invoked at the same point the old code
        // awaited it, so ordering relative to the visual playback stays the
        // same) but only awaited after the gate is released below. The gate
        // also guards PresentationCoordinator.PresentAsync on the tick path;
        // holding it across a potentially multi-second audio wait would
        // block that unrelated tick for the duration of the cue.
        Task? audioTask = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (requireIdle && _pet.Current.State != PetState.Idle)
            {
                return false;
            }

            using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? companionTask = null;
            try
            {
                var oneShot = _pet.Handle(petEvent);
                var playback = _playAsync(oneShot, _options(), playbackCancellation.Token);
                if (companion is not null)
                {
                    companionTask = ObserveCompanionAsync(
                        InvokeCompanionAsync(companion, playbackCancellation.Token));
                }

                var timeout = _delayAsync(_maximumDuration, timeoutCancellation.Token);
                if (await Task.WhenAny(playback, timeout) == playback)
                {
                    timeoutCancellation.Cancel();
                    await playback;
                    if (companionTask is not null)
                    {
                        // Let a glide that runs a hair longer than the clip land
                        // before idle is restored. Never throws (observed).
                        await companionTask;
                    }

                    if (_playAudioAsync is not null)
                    {
                        // Wrapped in ObserveAudioAsync eagerly, right here
                        // under the gate, so it starts observing (and, on
                        // failure, reporting) the cue immediately. If the
                        // finally block below throws, the exception skips
                        // straight past the "await audioTask" further down,
                        // and an audioTask still bare at that point would go
                        // unobserved; a task already wrapped keeps consuming
                        // its own exception regardless.
                        audioTask = ObserveAudioAsync(
                            InvokeAudioAsync(() => _playAudioAsync(oneShot, cancellationToken)));
                    }
                }
                else
                {
                    playbackCancellation.Cancel();
                    _ = ObserveCanceledPlaybackAsync(playback);
                    await timeout;
                }
            }
            finally
            {
                if (companionTask is not null && !companionTask.IsCompleted)
                {
                    // Timed out or faulted: stop the companion where it is.
                    playbackCancellation.Cancel();
                    await companionTask;
                }

                _pet.Handle(new PetEvent.PresentationAcknowledged());
                _pet.Handle(completionEvent);
                _ = ObserveAmbientAsync(_playAsync(
                    _pet.Current,
                    _options(),
                    CancellationToken.None));
            }
        }
        finally
        {
            _gate.Release();
        }

        if (audioTask is not null)
        {
            // Already wrapped in ObserveAudioAsync above; awaiting it here
            // never throws.
            await audioTask;
        }

        return true;
    }

    private static Task InvokeCompanionAsync(
        Func<CancellationToken, Task> companion,
        CancellationToken cancellationToken)
    {
        try { return companion(cancellationToken); }
        catch (Exception exception) { return Task.FromException(exception); }
    }

    private static async Task ObserveCompanionAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError(
                "Dudu one-shot companion motion failed: {0}",
                exception.GetType().FullName);
        }
    }

    /// <summary>
    /// Invokes <paramref name="operation"/> immediately, converting a
    /// synchronous throw into a faulted task instead of letting it escape
    /// here -- the caller invokes this under the shared gate but awaits the
    /// result afterward, so a synchronous exception must not propagate
    /// before the gate's own cleanup (acknowledgement, ambient restore) runs.
    /// That cleanup does not wait for the cue: the acknowledgement and
    /// ambient restore run concurrently with it rather than after it. That
    /// is accepted behaviour, not a bug -- the cue is bounded and observed
    /// independently (see <see cref="ObserveAudioAsync"/>).
    /// </summary>
    private static Task InvokeAudioAsync(Func<Task> operation)
    {
        try { return operation(); }
        catch (Exception exception) { return Task.FromException(exception); }
    }

    private static async Task ObserveAudioAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError(
                "Dudu audio cue playback failed: {0}",
                exception.GetType().FullName);
        }
    }

    private static async Task ObserveCanceledPlaybackAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError(
                "Dudu cancelled one-shot animation failed: {0}",
                exception);
        }
    }

    private static async Task ObserveAmbientAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu ambient animation restore failed: {0}", exception);
        }
    }
}
