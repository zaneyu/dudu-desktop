using System.Diagnostics;
using System.Runtime.ExceptionServices;
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

    public TimeSpan ReducedMotionFadeDuration { get; init; } = TimeSpan.FromMilliseconds(120);
}

public sealed class AnimationEngine : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan MaximumSemanticDuration = TimeSpan.FromHours(1);
    private readonly object _stateGate = new();
    private readonly IFramePresenter _presenter;
    private readonly IAnimationClock _clock;
    private readonly SkiaFrameComposer _composer;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly DateOnly _localDate;
    private readonly SeasonalDates _seasonalDates;
    private AssetPack _pack;
    private Task? _activeTask;
    private CancellationTokenSource? _activeCancellation;
    private TaskCompletionSource<object?>? _disposeCompletion;
    private TaskCompletionSource<object?>? _mutationUsersCompletion;
    private int _mutationUsers;
    private bool _mutationDisposeRequested;
    private bool _disposed;
    private bool _resourcesDisposed;
    private bool _mutationGateDisposed;

    public AnimationEngine(
        AssetPack pack,
        IFramePresenter presenter,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null,
        DateOnly? localDate = null,
        SeasonalDates? seasonalDates = null)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clock = clock ?? new StopwatchAnimationClock();
        _composer = composer ?? new SkiaFrameComposer(pack);
        _localDate = localDate ?? DateOnly.FromDateTime(DateTime.Now);
        _seasonalDates = seasonalDates ?? SeasonalDates.Empty;
    }

    public AnimationEngine(
        IFramePresenter presenter,
        AssetPack pack,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null,
        DateOnly? localDate = null,
        SeasonalDates? seasonalDates = null)
        : this(pack, presenter, clock, composer, localDate, seasonalDates)
    {
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

            WaitForCompletion(active);
            lock (_stateGate)
            {
                ThrowIfDisposed();
                _pack = pack;
                _composer.SetPack(pack);
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
                catch (OperationCanceledException)
                {
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_stateGate)
            {
                _disposed = true;
                _activeCancellation?.Cancel();
            }

            // The run gate has been released by the inner finally before this catch.
            // Cleanup is best-effort so the presenter/composer exception remains primary.
            DisposeResources();
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
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
            var frame = animation.Frames[frameIndex];
            var frameDuration = TimeSpan.FromMilliseconds(frame.DurationMs);
            var frameEnd = AddDuration(frameStart, frameDuration, _clock.Frequency);
            var now = _clock.Timestamp;
            var isLastOneShotFrame = animation.Loop == "once" && frameIndex == animation.Frames.Count - 1;
            if (now > frameEnd || (now == frameEnd && !isLastOneShotFrame))
            {
                if (isLastOneShotFrame)
                {
                    return;
                }

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
        using var rendered = _composer.Compose(
            pack,
            animation,
            frame,
            scale,
            opacity,
            semanticDuration,
            frameDuration);
        await _presenter.PresentAsync(rendered, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WaitUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        while (_clock.Timestamp < deadline)
        {
            await _clock.DelayUntilAsync(deadline, cancellationToken).ConfigureAwait(false);
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
        var selectedOutfit = pack.ResolveOutfit(
            _localDate,
            _seasonalDates,
            outfitKey);

        if (pack.Manifest.Outfits.TryGetValue(selectedOutfit, out var selected)
            && selected.Animations.TryGetValue(animationKey, out var selectedAnimation))
        {
            return (selectedAnimation, selectedAnimation.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue(selectedOutfit, out selected)
            && selected.Animations.TryGetValue("idle", out var selectedIdle))
        {
            return (selectedIdle, selectedIdle.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue("base", out var baseOutfit)
            && baseOutfit.Animations.TryGetValue(animationKey, out var baseAnimation))
        {
            return (baseAnimation, baseAnimation.Frames[0].File);
        }

        if (pack.Manifest.Outfits.TryGetValue("base", out baseOutfit)
            && baseOutfit.Animations.TryGetValue("idle", out var baseIdle))
        {
            return (baseIdle, baseIdle.Frames[0].File);
        }

        throw new AssetManifestException("The manifest has no usable fallback idle animation.");
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

    private static void WaitForCompletion(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
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
            WaitForMutationUsersZero();
            DisposeResources();
            DisposeMutationGate();
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

            await WaitForMutationUsersZeroAsync().ConfigureAwait(false);
            DisposeResources();
            DisposeMutationGate();
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
