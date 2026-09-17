using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class AnimationEngineTests
{
    [Fact]
    public async Task One_shot_animation_preserves_semantic_duration_when_frames_are_late()
    {
        using var fixture = AnimationFixture.Create([100, 100, 100], loop: "once", animationKey: "celebrate");

        var play = fixture.Engine.PlayAsync(TestPresentation("celebrate"), AnimationOptions.Default, TestContext.Current.CancellationToken);
        fixture.Clock.AdvanceBy(250);
        fixture.Clock.AdvanceBy(50);
        await play;

        Assert.Equal(TimeSpan.FromMilliseconds(300), fixture.Presenter.SemanticDuration);
        Assert.InRange(fixture.Presenter.Frames.Count, 2, 3);
    }

    [Fact]
    public async Task Missing_animation_uses_fallback_idle_pose()
    {
        // Reduced motion holds the fallback pose for its semantic duration; nothing here
        // advances the manual clock, so it must complete waits by itself.
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle", packId: "fallback", immediateClock: true);

        await fixture.Engine.PlayAsync(TestPresentation("unknown"), AnimationOptions.ReducedMotion, TestContext.Current.CancellationToken);

        Assert.Equal("fallback/idle.png", fixture.Presenter.Single.Source);
    }

    [Fact]
    public async Task Automatic_outfit_refreshes_when_the_local_date_rolls_over()
    {
        var localDate = new DateOnly(2026, 11, 30);
        using var fixture = AnimationFixture.Create(
            [100],
            loop: "loop",
            animationKey: "idle",
            localDate: localDate,
            localDateProvider: () => localDate);
        using var cancellation = new CancellationTokenSource();
        var basePresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winterPresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Presenter.OnPresented = count =>
        {
            if (count == 1)
            {
                basePresented.TrySetResult(true);
            }
            else if (fixture.Presenter.Frames[^1].Source.EndsWith("winter.png", StringComparison.Ordinal))
            {
                winterPresented.TrySetResult(true);
            }
        };

        var play = fixture.Engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            cancellation.Token);
        await basePresented.Task.WaitAsync(TestContext.Current.CancellationToken);

        localDate = new DateOnly(2026, 12, 1);
        fixture.Clock.AdvanceBy(100);
        await winterPresented.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
    }

    [Fact]
    public async Task Looping_animation_stops_on_cancellation()
    {
        using var fixture = AnimationFixture.Create([100], loop: "loop", animationKey: "idle");
        using var cancellation = new CancellationTokenSource();
        var play = fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default, cancellation.Token);

        fixture.Clock.AdvanceBy(100);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        Assert.NotEmpty(fixture.Presenter.Frames);
    }

    [Fact]
    public async Task Pause_suspends_frame_advancement_until_resume_is_called()
    {
        // Task 23 performance requirement: ambient animation ticks must suspend while hidden,
        // fullscreen-suppressed, session-locked, or display-off (AnimationEngine.Pause/Resume).
        // This proves Pause() blocks the frame loop independently of clock time -- advancing the
        // manual clock while paused must not be enough on its own to reach the next frame -- and
        // that Resume() releases exactly that suspension.
        using var fixture = AnimationFixture.Create([100, 100, 100], loop: "loop", animationKey: "idle");
        using var cancellation = new CancellationTokenSource();
        var firstFramePresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFramePresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Presenter.OnPresented = count =>
        {
            if (count == 1)
            {
                // Pausing here -- before the play loop ever waits on the clock for frame 1 --
                // means the clock advance below has nothing registered to resolve.
                fixture.Engine.Pause();
                firstFramePresented.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondFramePresented.TrySetResult(true);
            }
        };

        var play = fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default, cancellation.Token);
        await firstFramePresented.Task.WaitAsync(TestContext.Current.CancellationToken);

        fixture.Clock.AdvanceBy(100);
        Assert.Single(fixture.Presenter.Frames);

        fixture.Engine.Resume();
        await secondFramePresented.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Presenter.Frames.Count);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
    }

    [Fact]
    public async Task A_replacement_cancels_the_previous_play_before_presenting_the_new_one()
    {
        using var fixture = AnimationFixture.Create([100], loop: "loop", animationKey: "idle");
        var first = fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default, TestContext.Current.CancellationToken);
        var second = fixture.Engine.PlayAsync(TestPresentation("celebrate"), AnimationOptions.Default, TestContext.Current.CancellationToken);

        fixture.Clock.AdvanceBy(100);
        fixture.Clock.AdvanceBy(100);
        await second;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Contains(fixture.Presenter.Frames, frame => frame.Source.EndsWith("/celebrate.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reduced_motion_can_fade_without_changing_the_semantic_duration()
    {
        using var fixture = AnimationFixture.Create([100, 100], loop: "once", animationKey: "celebrate");
        var options = AnimationOptions.ReducedMotion with { FadeReducedMotion = true };
        var play = fixture.Engine.PlayAsync(TestPresentation("celebrate"), options, TestContext.Current.CancellationToken);

        fixture.Clock.AdvanceBy(120);
        fixture.Clock.AdvanceBy(80);
        await play;

        Assert.Equal(TimeSpan.FromMilliseconds(200), fixture.Presenter.SemanticDuration);
        Assert.Equal(0f, fixture.Presenter.Frames[0].Opacity);
        Assert.Equal(1f, fixture.Presenter.Frames[^1].Opacity);
        Assert.Contains(fixture.Presenter.Frames, frame => frame.Opacity > 0f && frame.Opacity < 1f);
    }

    [Fact]
    public async Task Replacing_the_current_pose_with_reduced_motion_updates_live_mode()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle", immediateClock: true);

        await fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.ReducedMotion, TestContext.Current.CancellationToken);

        Assert.True(fixture.Engine.CurrentOptions.ReducedMotionEnabled);
        Assert.Equal(TestPresentation("idle"), fixture.Engine.CurrentPresentation);
        Assert.NotEmpty(fixture.Presenter.Frames);
    }

    [Fact]
    public async Task Repaint_requests_are_coalesced_and_never_present_concurrently()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle", immediateClock: true);
        var presenter = new ConcurrencyTrackingPresenter();
        var composer = new SkiaFrameComposer(fixture.Pack);
        var engine = new AnimationEngine(
            fixture.Pack,
            presenter,
            new ManualAnimationClock(advanceOnWait: true),
            composer);
        await engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.ReducedMotion,
            TestContext.Current.CancellationToken);

        presenter.Block = true;
        for (var index = 0; index < 20; index++)
        {
            composer.SetOverlayPalette(OverlaySurfacePalette.For(AppTheme.Light, highContrast: false));
        }
        await presenter.RepaintStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        for (var index = 0; index < 20; index++)
        {
            composer.SetOverlayPalette(OverlaySurfacePalette.For(AppTheme.Dark, highContrast: false));
        }

        presenter.Release.TrySetResult(true);
        await engine.DisposeAsync();

        Assert.Equal(1, presenter.MaximumConcurrentPresentations);
        Assert.InRange(presenter.PresentationCount, 2, 3);
    }

    [Fact]
    public async Task Invalid_scale_is_rejected_before_playback()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default with { Scale = 0 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cached_engine_presentations_measure_300_same_thread_frames_under_fifty_kib()
    {
        using var fixture = AnimationFixture.Create([1], loop: "loop", animationKey: "idle", immediateClock: true);
        using (fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0))
        {
        }

        // The gate measures the engine and composer, not this recorder's list growth
        // or the cancellation that ends the run: the list is sized up front and the
        // allocation counter is read on the 300th presentation, before cancelling.
        fixture.Presenter.Frames.Clear();
        fixture.Presenter.Frames.Capacity = 300;
        using var cancellation = new CancellationTokenSource();
        long afterFrames = 0;
        fixture.Presenter.OnPresented = count =>
        {
            if (count == 300)
            {
                afterFrames = GC.GetAllocatedBytesForCurrentThread();
                cancellation.Cancel();
            }
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var play = fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        var allocated = afterFrames - before;

        Assert.Equal(300, fixture.Presenter.Frames.Count);
        Assert.InRange(allocated, 0, 50 * 1024);
    }

    [Fact]
    public async Task Replace_pack_serializes_against_concurrent_play_creation()
    {
        using var source = AnimationFixture.Create([100], loop: "loop", animationKey: "idle", packId: "source");
        using var replacement = AnimationFixture.Create([100], loop: "loop", animationKey: "idle", packId: "replacement");
        var presenter = new BlockingFramePresenter();
        using var engine = new AnimationEngine(source.Pack, presenter, source.Clock, new SkiaFrameComposer(source.Pack));

        var first = engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default, TestContext.Current.CancellationToken);
        await presenter.FirstPresentation.Task.WaitAsync(TestContext.Current.CancellationToken);

        var replace = Task.Run(() => engine.ReplacePack(replacement.Pack), TestContext.Current.CancellationToken);
        await presenter.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = Task.Run(
            async () => await engine.PlayAsync(
                TestPresentation("unknown"),
                AnimationOptions.ReducedMotion,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        presenter.ReleaseCancellation.TrySetResult(true);
        await replace;
        await presenter.SecondPresentation.Task.WaitAsync(TestContext.Current.CancellationToken);
        source.Clock.AdvanceBy(100);
        await second;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Equal("replacement/idle.png", presenter.Sources[^1]);
    }

    [Fact]
    public async Task Async_presenter_must_finish_borrowed_byte_use_before_completion()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle");
        var presenter = new DeferredFramePresenter();
        using var engine = new AnimationEngine(fixture.Pack, presenter, fixture.Clock, fixture.Composer);
        var play = engine.PlayAsync(TestPresentation("idle"), AnimationOptions.ReducedMotion, TestContext.Current.CancellationToken);

        await presenter.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        presenter.Release.TrySetResult(true);
        fixture.Clock.AdvanceBy(100);
        await play;

        Assert.Equal(presenter.ByteBeforeCompletion, presenter.ByteAfterDeferredRead);
        Assert.NotNull(presenter.Frame);
        Assert.True(presenter.Frame!.IsDisposed);
    }

    [Fact]
    public async Task Presenter_fault_preserves_the_original_exception_and_engine_recovers()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle", immediateClock: true);
        var composer = fixture.Composer;
        var presenter = new FaultingFramePresenter();
        using var engine = new AnimationEngine(
            fixture.Pack,
            presenter,
            fixture.Clock,
            composer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken));

        Assert.Equal("presenter failed", exception.Message);
        Assert.False(composer.IsDisposed);

        // A playback fault must not latch the engine as disposed, and the
        // faulted predecessor must not poison the replacement playback.
        presenter.Fail = false;
        await engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(presenter.Presented > 0);
    }

    [Fact]
    public async Task Presenter_fault_followed_by_async_disposal_is_non_throwing_and_single_flight()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle");
        var composer = fixture.Composer;
        await using var engine = new AnimationEngine(
            fixture.Pack,
            new FaultingFramePresenter(),
            fixture.Clock,
            composer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken));

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => engine.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);

        Assert.Equal("presenter failed", exception.Message);
        Assert.Equal(1, composer.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_async_disposal_is_single_flight_while_playback_is_canceled()
    {
        using var fixture = AnimationFixture.Create([100], loop: "loop", animationKey: "idle");
        var presenter = new BlockingFramePresenter();
        var composer = new SkiaFrameComposer(fixture.Pack);
        using var engine = new AnimationEngine(
            fixture.Pack,
            presenter,
            fixture.Clock,
            composer);
        var play = engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken);
        await presenter.FirstPresentation.Task.WaitAsync(TestContext.Current.CancellationToken);

        var firstDispose = engine.DisposeAsync().AsTask();
        await presenter.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondDispose = engine.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        presenter.ReleaseCancellation.TrySetResult(true);
        await Task.WhenAll(firstDispose, secondDispose);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        Assert.Equal(1, composer.DisposeCount);
    }

    [Fact]
    public async Task Synchronous_and_async_disposal_interleave_without_races()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle");
        var presenter = new BlockingFramePresenter();
        var composer = new SkiaFrameComposer(fixture.Pack);
        using var engine = new AnimationEngine(
            fixture.Pack,
            presenter,
            fixture.Clock,
            composer);
        var play = engine.PlayAsync(
            TestPresentation("idle"),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken);
        await presenter.FirstPresentation.Task.WaitAsync(TestContext.Current.CancellationToken);

        var synchronous = Task.Run(engine.Dispose, TestContext.Current.CancellationToken);
        await presenter.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var asynchronous = engine.DisposeAsync().AsTask();

        Assert.False(synchronous.IsCompleted);
        Assert.False(asynchronous.IsCompleted);
        presenter.ReleaseCancellation.TrySetResult(true);
        await Task.WhenAll(synchronous, asynchronous);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        Assert.Equal(1, composer.DisposeCount);
    }

    private static PetPresentation TestPresentation(string key) =>
        new(PetState.Ambient, key, null, null, false);

    private sealed class AnimationFixture : IDisposable
    {
        private readonly string _root;

        private AnimationFixture(
            string root,
            AnimationEngine engine,
            RecordingFramePresenter presenter,
            ManualAnimationClock clock,
            SkiaFrameComposer composer,
            AssetPack pack)
        {
            _root = root;
            Engine = engine;
            Presenter = presenter;
            Clock = clock;
            Composer = composer;
            Pack = pack;
        }

        public AnimationEngine Engine { get; }

        public RecordingFramePresenter Presenter { get; }

        public ManualAnimationClock Clock { get; }

        public SkiaFrameComposer Composer { get; }

        public AssetPack Pack { get; }

        public AssetAnimation Animation => Pack.Manifest.Outfits["base"].Animations["idle"];

        public static AnimationFixture Create(
            IReadOnlyList<int> durations,
            string loop,
            string animationKey,
            string packId = "fixture",
            bool immediateClock = false,
            DateOnly? localDate = null,
            Func<DateOnly>? localDateProvider = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-animation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "idle.png"), PngFixture.OnePixel);
            File.WriteAllBytes(Path.Combine(root, "celebrate.png"), PngFixture.OnePixel);
            File.WriteAllBytes(Path.Combine(root, "winter.png"), PngFixture.OnePixel);

            var frames = durations
                .Select(_ => new AssetFrame { File = animationKey == "idle" ? "idle.png" : "celebrate.png", DurationMs = _ })
                .ToList();
            var animation = new AssetAnimation
            {
                Frames = frames,
                Loop = loop,
                NominalSize = new PixelSize(1, 1),
                Anchor = new PixelPoint(0, 0),
                ReducedMotion = frames[0].File,
            };
            var idle = new AssetAnimation
            {
                Frames = [new AssetFrame { File = "idle.png", DurationMs = 100 }],
                Loop = "loop",
                NominalSize = new PixelSize(1, 1),
                Anchor = new PixelPoint(0, 0),
                ReducedMotion = "idle.png",
            };
            var animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
            {
                ["idle"] = animationKey == "idle" ? animation : idle,
            };
            if (animationKey != "idle")
            {
                animations[animationKey] = animation;
            }
            else
            {
                animations["celebrate"] = new AssetAnimation
                {
                    Frames = [new AssetFrame { File = "celebrate.png", DurationMs = 100 }],
                    Loop = "once",
                    NominalSize = new PixelSize(1, 1),
                    Anchor = new PixelPoint(0, 0),
                    ReducedMotion = "celebrate.png",
                };
            }
            var manifest = new AssetManifest
            {
                SchemaVersion = 1,
                PackId = packId,
                Version = "1",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "test" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit { Animations = animations },
                    ["winter"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            ["idle"] = new AssetAnimation
                            {
                                Frames = [new AssetFrame { File = "winter.png", DurationMs = 100 }],
                                Loop = "loop",
                                NominalSize = new PixelSize(1, 1),
                                Anchor = new PixelPoint(0, 0),
                                ReducedMotion = "winter.png",
                            },
                        },
                    },
                },
            };
            var pack = new AssetPack(Path.Combine(root, "manifest.json"), manifest);
            var presenter = new RecordingFramePresenter();
            var clock = new ManualAnimationClock(immediateClock);
            var composer = new SkiaFrameComposer(pack);
            return new AnimationFixture(
                root,
                new AnimationEngine(
                    pack,
                    presenter,
                    clock,
                    composer,
                    localDate,
                    SeasonalDates.Empty,
                    localDateProvider),
                presenter,
                clock,
                composer,
                pack);
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class RecordingFramePresenter : IFramePresenter
    {
        public List<PresentedFrame> Frames { get; } = [];

        public Action<int>? OnPresented { get; set; }

        public TimeSpan SemanticDuration => Frames.Count == 0 ? TimeSpan.Zero : Frames[0].SemanticDuration;

        public PresentedFrame Single => Assert.Single(Frames);

        public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(new PresentedFrame(frame.Source, frame.Opacity, frame.SemanticDuration));
            OnPresented?.Invoke(Frames.Count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingFramePresenter : IFramePresenter
    {
        public TaskCompletionSource<bool> FirstPresentation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondPresentation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Sources { get; } = [];

        private int _count;

        public async ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            Sources.Add(frame.Source);
            var count = Interlocked.Increment(ref _count);
            if (count == 1)
            {
                FirstPresentation.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved.TrySetResult(true);
                    await ReleaseCancellation.Task;
                    throw;
                }
            }
            else
            {
                SecondPresentation.TrySetResult(true);
            }
        }
    }

    private sealed class DeferredFramePresenter : IFramePresenter
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RenderedFrame? Frame { get; private set; }

        public byte ByteBeforeCompletion { get; private set; }

        public byte ByteAfterDeferredRead { get; private set; }

        public async ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            Frame = frame;
            cancellationToken.ThrowIfCancellationRequested();
            ByteBeforeCompletion = frame.Bytes.Span[0];
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            ByteAfterDeferredRead = frame.Bytes.Span[0];
        }
    }

    private sealed class FaultingFramePresenter : IFramePresenter
    {
        public bool Fail { get; set; } = true;

        public int Presented { get; private set; }

        public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("presenter failed");
            }

            Presented++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrencyTrackingPresenter : IFramePresenter
    {
        private int _active;
        private int _maximum;
        private int _count;

        public bool Block { get; set; }
        public TaskCompletionSource<bool> RepaintStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumConcurrentPresentations => Volatile.Read(ref _maximum);
        public int PresentationCount => Volatile.Read(ref _count);

        public async ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _count);
            UpdateMaximum(active);
            try
            {
                if (Block)
                {
                    RepaintStarted.TrySetResult(true);
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximum);
                if (value <= current || Interlocked.CompareExchange(ref _maximum, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private readonly record struct PresentedFrame(string Source, float Opacity, TimeSpan SemanticDuration);

    private sealed class ManualAnimationClock : IAnimationClock
    {
        private readonly bool _advanceOnWait;
        private readonly object _gate = new();
        private readonly List<(long Deadline, TaskCompletionSource<bool> Completion)> _waiters = [];
        private long _timestamp;

        public ManualAnimationClock(bool advanceOnWait = false)
        {
            _advanceOnWait = advanceOnWait;
        }

        public long Timestamp => Interlocked.Read(ref _timestamp);

        public long Frequency => 1000;

        public ValueTask DelayUntilAsync(long deadline, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_advanceOnWait && Timestamp < deadline)
                {
                    Interlocked.Exchange(ref _timestamp, deadline);
                    return ValueTask.CompletedTask;
                }

                if (Timestamp >= deadline)
                {
                    return ValueTask.CompletedTask;
                }

                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((deadline, completion));
                return new ValueTask(completion.Task.WaitAsync(cancellationToken));
            }
        }

        public void AdvanceBy(int milliseconds)
        {
            Interlocked.Add(ref _timestamp, milliseconds);
            List<TaskCompletionSource<bool>> ready;
            lock (_gate)
            {
                ready = _waiters.Where(waiter => waiter.Deadline <= Timestamp).Select(waiter => waiter.Completion).ToList();
                _waiters.RemoveAll(waiter => waiter.Deadline <= Timestamp);
            }

            foreach (var completion in ready)
            {
                completion.TrySetResult(true);
            }
        }
    }

    private static class PngFixture
    {
        public static readonly byte[] OnePixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    }
}
