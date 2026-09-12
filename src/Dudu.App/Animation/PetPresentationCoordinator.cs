using Dudu.Core.Models;
using Dudu.Core.Pet;

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
    private readonly Func<AnimationOptions> _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _maximumDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PetPresentationCoordinator(
        PetStateMachine pet,
        Func<PetPresentation, AnimationOptions, CancellationToken, Task> playAsync,
        Func<AnimationOptions>? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? maximumDuration = null)
    {
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _options = options ?? (() => AnimationOptions.Default);
        _delayAsync = delayAsync ?? Task.Delay;
        _maximumDuration = maximumDuration ?? DefaultMaximumDuration;
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
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
