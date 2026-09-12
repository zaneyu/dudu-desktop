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

    public TimeSpan ReducedMotionFadeDuration { get; init; } = TimeSpan.FromMilliseconds(120);
}

public sealed class AnimationEngine : IDisposable, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly IFramePresenter _presenter;
    private readonly IAnimationClock _clock;
    private readonly SkiaFrameComposer _composer;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private AssetPack _pack;
    private Task? _activeTask;
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public AnimationEngine(
        AssetPack pack,
        IFramePresenter presenter,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clock = clock ?? new StopwatchAnimationClock();
        _composer = composer ?? new SkiaFrameComposer(pack);
    }

    public AnimationEngine(
        IFramePresenter presenter,
        AssetPack pack,
        IAnimationClock? clock = null,
        SkiaFrameComposer? composer = null)
        : this(pack, presenter, clock, composer)
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

    public void ReplacePack(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
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

    public void Dispose()
    {
        Task? active;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeCancellation?.Cancel();
            active = _activeTask;
        }

        WaitForCompletion(active);
        _composer.Dispose();
        _runGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Task? active;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeCancellation?.Cancel();
            active = _activeTask;
        }

        if (active is not null)
        {
            try
            {
                await active.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _composer.Dispose();
        _runGate.Dispose();
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
                resolved.SourcePath,
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
        string sourcePath,
        AnimationOptions options,
        TimeSpan semanticDuration,
        long start,
        CancellationToken cancellationToken)
    {
        var frame = animation.Frames[0];
        var reducedPath = animation.ReducedMotion ?? frame.File;
        var fadeDuration = options.FadeReducedMotion
            ? options.ReducedMotionFadeDuration
            : TimeSpan.Zero;
        var visibleDuration = semanticDuration > fadeDuration ? semanticDuration : fadeDuration;

        await PresentAsync(
            pack,
            animation,
            new AssetFrame { File = reducedPath, DurationMs = Math.Max(1, (int)Math.Min(int.MaxValue, semanticDuration.TotalMilliseconds)) },
            semanticDuration,
            semanticDuration,
            options.Scale,
            options.FadeReducedMotion ? 0f : 1f,
            cancellationToken).ConfigureAwait(false);

        if (fadeDuration > TimeSpan.Zero)
        {
            await WaitUntilAsync(
                AddDuration(start, fadeDuration, _clock.Frequency),
                cancellationToken).ConfigureAwait(false);
            await PresentAsync(
                pack,
                animation,
                new AssetFrame { File = reducedPath, DurationMs = Math.Max(1, (int)Math.Min(int.MaxValue, semanticDuration.TotalMilliseconds)) },
                semanticDuration,
                semanticDuration,
                options.Scale,
                1f,
                cancellationToken).ConfigureAwait(false);
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

    private static (AssetAnimation Animation, string SourcePath) ResolveAnimation(
        AssetPack pack,
        string animationKey,
        string? outfitKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        var selectedOutfit = pack.ResolveOutfit(
            DateOnly.FromDateTime(DateTime.Now),
            SeasonalDates.Empty,
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
        long milliseconds = 0;
        foreach (var frame in animation.Frames)
        {
            milliseconds = checked(milliseconds + frame.DurationMs);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static long AddDuration(long timestamp, TimeSpan duration, long frequency)
    {
        var ticks = duration.TotalSeconds * frequency;
        if (ticks >= long.MaxValue - timestamp)
        {
            return long.MaxValue;
        }

        return timestamp + Math.Max(1L, (long)Math.Ceiling(ticks));
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
