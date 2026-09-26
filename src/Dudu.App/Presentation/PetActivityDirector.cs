using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Pet;

namespace Dudu.App.Presentation;

/// <summary>
/// Keeps Dudu moving between notes: every few seconds it asks
/// <see cref="PetActivityScheduler"/> whether an idle fidget or a short wander is
/// due, and plays it through the shared one-shot path only while the pet is
/// plainly idle. Silent (motion clips have no audio cue), never preempts a note,
/// reminder, comfort, or focus, and fully off under reduced motion.
/// </summary>
public sealed class PetActivityDirector : IAsyncDisposable
{
    public const string ActivityOperation = "pet-activity";

    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(5);

    /// <summary>Default glide length when the pack does not say how long its walk clip is.</summary>
    public static readonly TimeSpan DefaultWanderDuration = TimeSpan.FromMilliseconds(2400);

    /// <summary>A glide always ends inside the one-shot presentation cap.</summary>
    public static readonly TimeSpan MaximumWanderDuration = TimeSpan.FromMilliseconds(2800);

    private static readonly TimeSpan MinimumWanderDuration = TimeSpan.FromMilliseconds(500);

    private readonly PetActivityScheduler _scheduler;
    private readonly Func<PetActivityGate> _readGate;
    private readonly Func<PetEvent, string, Func<CancellationToken, Task>?, CancellationToken, Task<bool>> _presentIdleOneShotAsync;
    private readonly Func<int, int, CancellationToken, Task<OverlayWanderPlan?>> _planWanderAsync;
    private readonly Func<OverlayWanderPlan, TimeSpan, CancellationToken, Task<bool>> _glideAsync;
    private readonly TimeSpan _wanderDuration;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _tickInterval;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <param name="presentIdleOneShotAsync">
    /// <see cref="Dudu.App.Animation.PetPresentationCoordinator.TryPresentIdleOneShotAsync"/>:
    /// plays the clip only if the pet is idle under the shared pet gate.
    /// </param>
    /// <param name="planWanderAsync">Plans a walk (direction, nominal distance) or returns null to stay put.</param>
    /// <param name="glideAsync">Moves the window along a planned walk while the walk clip plays.</param>
    public PetActivityDirector(
        PetActivityScheduler scheduler,
        Func<PetActivityGate> readGate,
        Func<PetEvent, string, Func<CancellationToken, Task>?, CancellationToken, Task<bool>> presentIdleOneShotAsync,
        Func<int, int, CancellationToken, Task<OverlayWanderPlan?>> planWanderAsync,
        Func<OverlayWanderPlan, TimeSpan, CancellationToken, Task<bool>> glideAsync,
        TimeSpan? wanderDuration = null,
        IAppHostErrorReporter? errorReporter = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? tickInterval = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _readGate = readGate ?? throw new ArgumentNullException(nameof(readGate));
        _presentIdleOneShotAsync = presentIdleOneShotAsync
            ?? throw new ArgumentNullException(nameof(presentIdleOneShotAsync));
        _planWanderAsync = planWanderAsync ?? throw new ArgumentNullException(nameof(planWanderAsync));
        _glideAsync = glideAsync ?? throw new ArgumentNullException(nameof(glideAsync));
        _wanderDuration = ClampWanderDuration(wanderDuration ?? DefaultWanderDuration);
        _errorReporter = errorReporter;
        _delayAsync = delayAsync ?? Task.Delay;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        if (_tickInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tickInterval));
        }
    }

    public TimeSpan WanderDuration => _wanderDuration;

    /// <summary>
    /// The glide length matching the pack's walk clip, so Dudu's feet stop when
    /// the window does.
    /// </summary>
    public static TimeSpan WanderDurationFor(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Manifest.Outfits.TryGetValue("base", out var outfit)
            && outfit.Animations.TryGetValue(AssetManifestContract.WalkAnimationKey, out var walk)
            && walk.Frames.Count > 0)
        {
            return ClampWanderDuration(TimeSpan.FromMilliseconds(walk.Frames.Sum(frame => (long)frame.DurationMs)));
        }

        return DefaultWanderDuration;
    }

    /// <summary>The animation keys the pack's base outfit ships (motion clips live there).</summary>
    public static IReadOnlyList<string> AvailableAnimationKeys(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Manifest.Outfits.TryGetValue("base", out var outfit)
            ? outfit.Animations.Keys.ToArray()
            : [];
    }

    /// <summary>Starts the background loop once; later calls are ignored.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _stopping is not null || !_scheduler.HasActivities)
            {
                return;
            }

            _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = RunAsync(_stopping.Token);
        }
    }

    /// <summary>
    /// One scheduling pass. Returns the activity that actually played, or null
    /// when nothing was due, motion was suppressed, the pet was busy, or there
    /// was no room to walk. Failures are reported, never thrown (except the
    /// caller's own cancellation).
    /// </summary>
    public async Task<PetActivity?> TickAsync(CancellationToken cancellationToken = default)
    {
        PetActivity? activity;
        try
        {
            activity = _scheduler.TryGetNext(_readGate());
        }
        catch (Exception exception)
        {
            Report(exception);
            return null;
        }

        if (activity is null)
        {
            return null;
        }

        try
        {
            var played = activity.Kind == PetActivityKind.Wander
                ? await WanderAsync(activity, cancellationToken).ConfigureAwait(false)
                : await _presentIdleOneShotAsync(
                    new PetEvent.AmbientRequested(activity.AnimationKey),
                    activity.AnimationKey,
                    null,
                    cancellationToken).ConfigureAwait(false);
            return played ? activity : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(exception);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stopping;
        Task loop;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopping = _stopping;
            loop = _loop;
        }

        if (stopping is null)
        {
            return;
        }

        stopping.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _delayAsync(_tickInterval, cancellationToken).ConfigureAwait(false);
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // TickAsync reports its own failures; this only guards the delay.
            Report(exception);
        }
    }

    private async Task<bool> WanderAsync(PetActivity activity, CancellationToken cancellationToken)
    {
        var plan = await _planWanderAsync(activity.Direction, activity.Distance, cancellationToken)
            .ConfigureAwait(false);
        if (plan is not { } route)
        {
            return false;
        }

        return await _presentIdleOneShotAsync(
            new PetEvent.AmbientRequested(activity.AnimationKey),
            activity.AnimationKey,
            token => _glideAsync(route, _wanderDuration, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan ClampWanderDuration(TimeSpan duration) =>
        duration < MinimumWanderDuration
            ? MinimumWanderDuration
            : duration > MaximumWanderDuration
                ? MaximumWanderDuration
                : duration;

    private void Report(Exception exception)
    {
        if (_errorReporter is not null)
        {
            try
            {
                _errorReporter.Report(ActivityOperation, exception);
                return;
            }
            catch
            {
                // Fall through to the Trace line below.
            }
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            ActivityOperation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
