using System.Diagnostics;
using Dudu.Core.Assets;
using Dudu.Core.Models;

namespace Dudu.App.Animation;

public interface IAnimationClock
{
    long Timestamp { get; }

    long Frequency { get; }

    ValueTask DelayUntilAsync(long deadline, CancellationToken cancellationToken);
}

public sealed class StopwatchAnimationClock : IAnimationClock
{
    public long Timestamp => Stopwatch.GetTimestamp();

    public long Frequency => Stopwatch.Frequency;

    public ValueTask DelayUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return ValueTask.CompletedTask;
        }

        var milliseconds = remaining * 1000d / Stopwatch.Frequency;
        return new ValueTask(Task.Delay(
            TimeSpan.FromMilliseconds(Math.Max(1d, milliseconds)),
            cancellationToken));
    }
}

public sealed record AnimationOptions
{
    public static AnimationOptions Default { get; } = new();

    public static AnimationOptions ReducedMotion { get; } = new() { ReducedMotionEnabled = true };

    public double Scale { get; init; } = 1d;

    public bool ReducedMotionEnabled { get; init; }

    public string? OutfitKey { get; init; }

    public bool FadeReducedMotion { get; init; }

    public TimeSpan ReducedMotionFadeDuration { get; init; } = TimeSpan.FromMilliseconds(90);
}

public sealed class AnimationEngine : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan MaximumSemanticDuration = TimeSpan.FromHours(1);
    private readonly object _stateGate = new();
    private readonly IFramePresenter _presenter;
    private readonly IAnimationClock _clock;
    private readonly SkiaFrameComposer _composer;
    private readonly Func<DateOnly>? _localDateProvider;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly object _repaintGate = new();
    private DateOnly _localDate;
    private SeasonalDates _seasonalDates;
    private AssetPack _pack;
    private Task? _activeTask;
    private CancellationTokenSource? _activeCancellation;
    private TaskCompletionSource<object?>? _disposeCompletion;
    private TaskCompletionSource<object?>? _mutationUsersCompletion;
    private AnimationOptions _currentOptions = AnimationOptions.Default;
    private PetPresentation? _currentPresentation;
    private PresentedFrameState? _lastPresented;
    private int _mutationUsers;
    private bool _mutationDisposeRequested;
    private bool _disposed;
    private bool _resourcesDisposed;
    private bool _mutationGateDisposed;
    private bool _presentationGateDisposed;
    private bool _repaintPending;
    private Task _repaintWorker = Task.CompletedTask;
    private bool _isPaused;
    private TaskCompletionSource<object?>? _pauseCompletion;

    public AnimationEngine(
        AssetPack pack,
        IFramePresenter presenter,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null,
        DateOnly? localDate = null,
        SeasonalDates? seasonalDates = null,
        Func<DateOnly>? localDateProvider = null)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clock = clock ?? new StopwatchAnimationClock();
        _composer = composer ?? new SkiaFrameComposer(pack);
        _composer.RepaintRequested += OnComposerRepaintRequested;
        _localDateProvider = localDateProvider;
        _localDate = localDate ?? DateOnly.FromDateTime(DateTime.Now);
        _seasonalDates = seasonalDates ?? SeasonalDates.Empty;
    }

    public AnimationEngine(
        IFramePresenter presenter,
        AssetPack pack,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null,
        DateOnly? localDate = null,
        SeasonalDates? seasonalDates = null,
        Func<DateOnly>? localDateProvider = null)
        : this(pack, presenter, clock, composer, localDate, seasonalDates, localDateProvider)
    {
    }

    public AnimationOptions CurrentOptions
    {
        get
        {
            lock (_stateGate)
            {
                return _currentOptions;
            }
        }
    }

    public PetPresentation? CurrentPresentation
    {
        get
        {
            lock (_stateGate)
            {
                return _currentPresentation;
            }
        }
    }

    /// <summary>
    /// Updates the calendar context used by automatic outfit resolution. The
    /// next repaint or presentation observes the new date without requiring a
    /// process restart or replacing the loaded asset pack.
    /// </summary>
    public void UpdateSeasonalContext(DateOnly localDate, SeasonalDates seasonalDates)
    {
        ArgumentNullException.ThrowIfNull(seasonalDates);
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _localDate = localDate;
            _seasonalDates = seasonalDates;
        }
    }

    public Task PlayAsync(
        PetPresentation presentation,
        AnimationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        options ??= AnimationOptions.Default;
        ValidateOptions(options);

        EnterMutation();
        var gateEntered = false;
        try
        {
            _mutationGate.Wait();
            gateEntered = true;
            Task? previous;
            CancellationTokenSource operationCancellation;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                previous = _activeTask;
                _activeCancellation?.Cancel();
                _currentPresentation = presentation;
                _currentOptions = options;
                operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCancellation = operationCancellation;
                var task = RunReplacementAsync(previous, presentation, options, operationCancellation);
                _activeTask = task;
                return task;
            }
        }
        finally
        {
            if (gateEntered)
            {
                _mutationGate.Release();
            }

            ExitMutation();
        }
    }

    /// <summary>Suspends frame-loop waits so ambient ticks stop while hidden,
    /// fullscreen-suppressed, session-locked, or the display is off. Leaves
    /// the active presentation in place rather than cancelling or restarting
    /// it, so callers never need a second suppression path: the existing
    /// runtime hooks call this instead of duplicating gate logic.</summary>
    public void Pause()
    {
        lock (_stateGate)
        {
            if (_disposed || _isPaused) return;
            _isPaused = true;
            _pauseCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Resumes frame-loop waits suspended by <see cref="Pause"/>.</summary>
    public void Resume()
    {
        TaskCompletionSource<object?>? completion;
        lock (_stateGate)
        {
            if (!_isPaused) return;
            _isPaused = false;
            completion = _pauseCompletion;
            _pauseCompletion = null;
        }

        completion?.TrySetResult(null);
    }

    public void ReplacePack(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        EnterMutation();
        var gateEntered = false;
        try
        {
            _mutationGate.Wait();
            gateEntered = true;
            Task? active;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                _activeCancellation?.Cancel();
                active = _activeTask;
            }

            WaitForCompletionIgnoringFault(active);
            _presentationGate.Wait();
            try
            {
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    _pack = pack;
                    _lastPresented = null;
                    _composer.SetPack(pack);
                }
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        finally
        {
            if (gateEntered)
            {
                _mutationGate.Release();
            }

            ExitMutation();
        }
    }

    public void Dispose()
    {
        var start = BeginDispose();
        if (!start.IsOwner)
        {
            start.Completion.Task.GetAwaiter().GetResult();
            return;
        }

        DisposeCore(start.Active, start.Completion);
    }

    public async ValueTask DisposeAsync()
    {
        var start = BeginDispose();
        if (!start.IsOwner)
        {
            await start.Completion.Task.ConfigureAwait(false);
            return;
        }

        await DisposeCoreAsync(start.Active, start.Completion).ConfigureAwait(false);
    }

    private async Task RunReplacementAsync(
        Task? previous,
        PetPresentation presentation,
        AnimationOptions options,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            if (previous is not null)
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch
                {
                    // A cancelled or faulted predecessor was already reported to
                    // its own caller; it must not poison the replacement.
                }
            }

            await _runGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
            try
            {
                AssetPack pack;
                lock (_stateGate)
                {
                    pack = _pack;
                }

                await RunAnimationAsync(pack, presentation, options, operationCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _runGate.Release();
            }
        }
        // Playback faults intentionally do not latch disposal or tear down shared
        // resources: the fault surfaces to this PlayAsync caller only, and the
        // next PlayAsync replaces the failed presentation.
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_activeCancellation, operationCancellation))
                {
                    _activeCancellation = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private async Task RunAnimationAsync(
        AssetPack pack,
        PetPresentation presentation,
        AnimationOptions options,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveAnimation(pack, presentation.AnimationKey, options.OutfitKey);
        var animation = resolved.Animation;
        var semanticDuration = GetSemanticDuration(animation);
        var start = _clock.Timestamp;

        if (options.ReducedMotionEnabled)
        {
            await RunReducedMotionAsync(
                pack,
                animation,
                options,
                semanticDuration,
                start,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var frameStart = start;
        var frameIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.OutfitKey is null && RefreshLocalDate())
            {
                resolved = ResolveAnimation(pack, presentation.AnimationKey, options.OutfitKey);
                animation = resolved.Animation;
                semanticDuration = GetSemanticDuration(animation);
                frameStart = _clock.Timestamp;
                frameIndex = 0;
                continue;
            }

            var frame = animation.Frames[frameIndex];
            var frameDuration = TimeSpan.FromMilliseconds(frame.DurationMs);
            var frameEnd = AddDuration(frameStart, frameDuration, _clock.Frequency);
            var now = _clock.Timestamp;
            var isTerminalFrame = IsTerminalFrame(animation, frameIndex);
            // Late frames are skipped to keep the semantic duration, but the
            // final pose of a one-shot ("once") or held ("hold") animation is
            // always presented (the wait after it returns immediately when
            // already late). Skipping it left the pet frozen on an
            // intermediate pose.
            if (!isTerminalFrame && now >= frameEnd)
            {
                frameStart = frameEnd;
                frameIndex++;
                if (frameIndex == animation.Frames.Count)
                {
                    if (animation.Loop == "once" || animation.Loop == "hold")
                    {
                        if (animation.Loop == "hold")
                        {
                            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }

                    frameIndex = 0;
                }

                continue;
            }

            await PresentAsync(
                pack,
                animation,
                frame,
                frameDuration,
                semanticDuration,
                options.Scale,
                1f,
                cancellationToken).ConfigureAwait(false);

            if (animation.Loop == "once" && frameIndex == animation.Frames.Count - 1)
            {
                await WaitUntilAsync(frameEnd, cancellationToken).ConfigureAwait(false);
                return;
            }

            frameStart = frameEnd;
            frameIndex++;
            if (frameIndex == animation.Frames.Count)
            {
                if (animation.Loop == "hold")
                {
                    await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                }

                if (animation.Loop == "once")
                {
                    return;
                }

                frameIndex = 0;
            }

            await WaitUntilAsync(frameStart, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsTerminalFrame(AssetAnimation animation, int frameIndex) =>
        animation.Loop is "once" or "hold" && frameIndex == animation.Frames.Count - 1;

    private async Task RunReducedMotionAsync(
        AssetPack pack,
        AssetAnimation animation,
        AnimationOptions options,
        TimeSpan semanticDuration,
        long start,
        CancellationToken cancellationToken)
    {
        var frame = animation.Frames[0];
        var reducedPath = animation.ReducedMotion ?? frame.File;
        var reducedFrame = string.Equals(reducedPath, frame.File, StringComparison.Ordinal)
            ? frame
            : new AssetFrame { File = reducedPath, DurationMs = frame.DurationMs };
        var fadeDuration = options.FadeReducedMotion
            ? options.ReducedMotionFadeDuration
            : TimeSpan.Zero;
        var visibleDuration = semanticDuration > fadeDuration ? semanticDuration : fadeDuration;

        await PresentAsync(pack, animation, reducedFrame, semanticDuration, semanticDuration, options.Scale,
            options.FadeReducedMotion ? 0f : 1f, cancellationToken).ConfigureAwait(false);

        if (fadeDuration > TimeSpan.Zero)
        {
            const int fadeSteps = 3;
            for (var step = 1; step <= fadeSteps; step++)
            {
                var fadePoint = TimeSpan.FromTicks(checked(fadeDuration.Ticks * step / fadeSteps));
                await WaitUntilAsync(AddDuration(start, fadePoint, _clock.Frequency), cancellationToken)
                    .ConfigureAwait(false);
                await PresentAsync(pack, animation, reducedFrame, semanticDuration, semanticDuration, options.Scale,
                    step / (float)fadeSteps, cancellationToken).ConfigureAwait(false);
            }
        }

        await WaitUntilAsync(
            AddDuration(start, visibleDuration, _clock.Frequency),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PresentAsync(
        AssetPack pack,
        AssetAnimation animation,
        AssetFrame frame,
        TimeSpan frameDuration,
        TimeSpan semanticDuration,
        double scale,
        float opacity,
        CancellationToken cancellationToken)
    {
        await _presentationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PresentUnderGateAsync(
                new PresentedFrameState(pack, animation, frame, frameDuration, semanticDuration, scale, opacity),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    /// <summary>Composes and presents one frame; the caller holds
    /// <see cref="_presentationGate"/>. Records it as the frame on screen so
    /// an interaction repaint can redraw exactly that frame.</summary>
    private async ValueTask PresentUnderGateAsync(
        PresentedFrameState state,
        CancellationToken cancellationToken)
    {
        using (var rendered = _composer.Compose(
            state.Pack,
            state.Animation,
            state.Frame,
            state.Scale,
            state.Opacity,
            state.SemanticDuration,
            state.FrameDuration))
        {
            await _presenter.PresentAsync(rendered, cancellationToken).ConfigureAwait(false);
        }

        lock (_stateGate)
        {
            _lastPresented = state;
        }
    }

    /// <summary>Everything needed to recompose the frame currently on screen.</summary>
    private readonly record struct PresentedFrameState(
        AssetPack Pack,
        AssetAnimation Animation,
        AssetFrame Frame,
        TimeSpan FrameDuration,
        TimeSpan SemanticDuration,
        double Scale,
        float Opacity);

    private void OnComposerRepaintRequested(object? sender, EventArgs args)
    {
        // A surface click can change labels, breathing guidance, or an error
        // while the current animation is waiting.  Recompose only the current
        // frame: do not cancel/restart an approved one-shot transaction.
        lock (_stateGate)
        {
            if (_disposed || _resourcesDisposed) return;
        }
        lock (_repaintGate)
        {
            _repaintPending = true;
            if (_repaintWorker.IsCompleted)
            {
                _repaintWorker = Task.Run(ObserveRepaintLoopAsync);
            }
        }
    }

    private async Task ObserveRepaintLoopAsync()
    {
        while (true)
        {
            lock (_repaintGate)
            {
                if (!_repaintPending) return;
                _repaintPending = false;
            }
            try
            {
                await RepaintCurrentAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Repaint is advisory, but the owned worker always observes it.
                Trace.TraceError("Dudu overlay repaint failed: {0}", exception);
            }
        }
    }

    private async Task RepaintCurrentAsync()
    {
        try
        {
            lock (_stateGate)
            {
                if (_disposed || _resourcesDisposed) return;
            }

            // Hold the presentation gate while choosing the frame, so the
            // frame loop cannot present a newer frame in between and have
            // this repaint put an older one back on screen.
            await _presentationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (SelectRepaintFrame() is not { } state) return;
                await PresentUnderGateAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException)
        {
            // Shutdown can win the race with an interaction repaint.
        }
    }

    /// <summary>
    /// The frame to recompose for an interaction repaint (bubble opened or
    /// closed, palette change): the frame and opacity currently on screen.
    /// Repainting frame 0 instead made the pet flicker to its first pose on
    /// every bubble toggle. Falls back to the presentation's first frame only
    /// when nothing from the current pack has been presented yet.
    /// </summary>
    private PresentedFrameState? SelectRepaintFrame()
    {
        AssetPack pack;
        PetPresentation? presentation;
        AnimationOptions options;
        PresentedFrameState? last;
        lock (_stateGate)
        {
            if (_disposed || _resourcesDisposed) return null;
            pack = _pack;
            presentation = _currentPresentation;
            options = _currentOptions;
            last = _lastPresented;
        }

        if (presentation is null) return null;
        if (last is { } previous && ReferenceEquals(previous.Pack, pack))
        {
            return previous;
        }

        var resolved = ResolveAnimation(pack, presentation.AnimationKey, options.OutfitKey);
        var animation = resolved.Animation;
        var sourceFrame = animation.Frames[0];
        var frame = options.ReducedMotionEnabled && animation.ReducedMotion is { } reducedPath
            && !string.Equals(reducedPath, sourceFrame.File, StringComparison.Ordinal)
                ? new AssetFrame { File = reducedPath, DurationMs = sourceFrame.DurationMs }
                : sourceFrame;
        return new PresentedFrameState(
            pack,
            animation,
            frame,
            TimeSpan.FromMilliseconds(frame.DurationMs),
            GetSemanticDuration(animation),
            options.Scale,
            1f);
    }

    private async ValueTask WaitUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        while (_clock.Timestamp < deadline)
        {
            await _clock.DelayUntilAsync(deadline, cancellationToken).ConfigureAwait(false);
            await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pauseTask;
            lock (_stateGate)
            {
                if (!_isPaused || _pauseCompletion is null) return;
                pauseTask = _pauseCompletion.Task;
            }

            await pauseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private (AssetAnimation Animation, string SourcePath) ResolveAnimation(
        AssetPack pack,
        string animationKey,
        string? outfitKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        RefreshLocalDate();
        DateOnly localDate;
        SeasonalDates seasonalDates;
        lock (_stateGate)
        {
            localDate = _localDate;
            seasonalDates = _seasonalDates;
        }
        var selectedOutfit = pack.ResolveOutfit(
            localDate,
            seasonalDates,
            outfitKey);

        if (pack.Manifest.Outfits.TryGetValue(selectedOutfit, out var selected)
            && selected.Animations.TryGetValue(animationKey, out var selectedAnimation))
        {
            return (selectedAnimation, selectedAnimation.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue("base", out var baseOutfit)
            && baseOutfit.Animations.TryGetValue(animationKey, out var baseAnimation))
        {
            return (baseAnimation, baseAnimation.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue(selectedOutfit, out selected)
            && selected.Animations.TryGetValue("idle", out var selectedIdle))
        {
            return (selectedIdle, selectedIdle.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue("base", out baseOutfit)
            && baseOutfit.Animations.TryGetValue("idle", out var baseIdle))
        {
            return (baseIdle, baseIdle.Frames[0].File);
        }

        throw new AssetManifestException("The manifest has no usable fallback idle animation.");
    }

    private bool RefreshLocalDate()
    {
        if (_localDateProvider is null)
        {
            return false;
        }

        var currentLocalDate = _localDateProvider();
        lock (_stateGate)
        {
            if (currentLocalDate == _localDate)
            {
                return false;
            }

            _localDate = currentLocalDate;
            return true;
        }
    }

    private static TimeSpan GetSemanticDuration(AssetAnimation animation)
    {
        if (animation.Frames.Count == 0)
        {
            throw new AssetManifestException("Animation must contain at least one frame.");
        }

        long milliseconds = 0;
        foreach (var frame in animation.Frames)
        {
            if (frame.DurationMs <= 0)
            {
                throw new AssetManifestException("Animation frame durations must be positive.");
            }

            milliseconds = checked(milliseconds + frame.DurationMs);
            if (milliseconds > MaximumSemanticDuration.TotalMilliseconds)
            {
                throw new AssetManifestException(
                    $"Animation semantic duration cannot exceed {MaximumSemanticDuration}.");
            }
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static long AddDuration(long timestamp, TimeSpan duration, long frequency)
    {
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var ticks = duration.TotalSeconds * frequency;
        if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks > long.MaxValue)
        {
            throw new AssetManifestException("Animation deadline exceeds the monotonic clock range.");
        }

        var delta = Math.Max(1L, checked((long)Math.Ceiling(ticks)));
        if (timestamp > long.MaxValue - delta)
        {
            throw new AssetManifestException("Animation deadline exceeds the monotonic clock range.");
        }

        return checked(timestamp + delta);
    }

    private static void ValidateOptions(AnimationOptions options)
    {
        if (double.IsNaN(options.Scale) || double.IsInfinity(options.Scale) || options.Scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Scale must be finite and greater than zero.");
        }

        if (options.ReducedMotionFadeDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Reduced-motion fade duration cannot be negative.");
        }

        if (options.ReducedMotionFadeDuration > MaximumSemanticDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Reduced-motion fade duration is too long.");
        }
    }

    private void EnterMutation()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            checked
            {
                _mutationUsers++;
            }
        }
    }

    private void ExitMutation()
    {
        lock (_stateGate)
        {
            _mutationUsers--;
            if (_mutationDisposeRequested && _mutationUsers == 0)
            {
                _mutationUsersCompletion?.TrySetResult(null);
            }
        }
    }

    private (bool IsOwner, TaskCompletionSource<object?> Completion, Task? Active) BeginDispose()
    {
        lock (_stateGate)
        {
            if (_disposeCompletion is not null)
            {
                return (false, _disposeCompletion, null);
            }

            _disposed = true;
            lock (_repaintGate) _repaintPending = false;
            if (_isPaused)
            {
                _isPaused = false;
                _pauseCompletion?.TrySetResult(null);
                _pauseCompletion = null;
            }

            try
            {
                _activeCancellation?.Cancel();
            }
            catch
            {
                // Disposal remains best-effort and must not mask the playback fault.
            }

            var completion = new TaskCompletionSource<object?>
                (TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
            _mutationDisposeRequested = true;
            if (_mutationUsers > 0)
            {
                _mutationUsersCompletion = new TaskCompletionSource<object?>
                    (TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return (true, completion, _activeTask);
        }
    }

    private void DisposeCore(Task? active, TaskCompletionSource<object?> completion)
    {
        try
        {
            WaitForCompletionIgnoringFault(active);
            WaitForRepaintCompletion();
            WaitForMutationUsersZero();
            DisposeResources();
            DisposeMutationGate();
            DisposePresentationGate();
        }
        catch
        {
            // Teardown is idempotent and non-throwing after a caller has observed a playback fault.
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    private async Task DisposeCoreAsync(Task? active, TaskCompletionSource<object?> completion)
    {
        try
        {
            if (active is not null)
            {
                try
                {
                    await active.ConfigureAwait(false);
                }
                catch
                {
                    // The original task remains faulted for its caller; teardown never rethrows it.
                }
            }

            await WaitForRepaintCompletionAsync().ConfigureAwait(false);
            await WaitForMutationUsersZeroAsync().ConfigureAwait(false);
            DisposeResources();
            DisposeMutationGate();
            DisposePresentationGate();
        }
        catch
        {
            // Teardown is idempotent and non-throwing after a caller has observed a playback fault.
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    private static void WaitForCompletionIgnoringFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch
        {
            // The active task's original exception is intentionally not propagated by teardown.
        }
    }

    private void WaitForMutationUsersZero()
    {
        Task? completion;
        lock (_stateGate)
        {
            completion = _mutationUsersCompletion?.Task;
        }

        completion?.GetAwaiter().GetResult();
    }

    private async Task WaitForMutationUsersZeroAsync()
    {
        Task? completion;
        lock (_stateGate)
        {
            completion = _mutationUsersCompletion?.Task;
        }

        if (completion is not null)
        {
            await completion.ConfigureAwait(false);
        }
    }

    private void DisposeMutationGate()
    {
        lock (_stateGate)
        {
            if (_mutationGateDisposed)
            {
                return;
            }

            _mutationGateDisposed = true;
        }

        try
        {
            _mutationGate.Dispose();
        }
        catch
        {
        }
    }

    private void WaitForRepaintCompletion()
    {
        Task worker;
        lock (_repaintGate) worker = _repaintWorker;
        WaitForCompletionIgnoringFault(worker);
    }

    private async Task WaitForRepaintCompletionAsync()
    {
        Task worker;
        lock (_repaintGate) worker = _repaintWorker;
        try { await worker.ConfigureAwait(false); } catch { }
    }

    private void DisposePresentationGate()
    {
        lock (_stateGate)
        {
            if (_presentationGateDisposed) return;
            _presentationGateDisposed = true;
        }
        try { _presentationGate.Dispose(); } catch { }
    }

    private void DisposeResources()
    {
        lock (_stateGate)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
        }

        try
        {
            _composer.RepaintRequested -= OnComposerRepaintRequested;
            _composer.Dispose();
        }
        catch
        {
        }
        finally
        {
            try
            {
                _runGate.Dispose();
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
