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
            using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var oneShot = _pet.Handle(petEvent);
                var playback = _playAsync(oneShot, _options(), playbackCancellation.Token);
                var timeout = _delayAsync(_maximumDuration, timeoutCancellation.Token);
                if (await Task.WhenAny(playback, timeout) == playback)
                {
                    timeoutCancellation.Cancel();
                    await playback;
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
            finally
            {
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
    }

    private static async Task ObserveAudioAsync(Func<Task> operation)
    {
        try { await operation(); }
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
