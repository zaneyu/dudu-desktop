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
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle", packId: "fallback");

        await fixture.Engine.PlayAsync(TestPresentation("unknown"), AnimationOptions.ReducedMotion, TestContext.Current.CancellationToken);

        Assert.Equal("fallback/idle.png", fixture.Presenter.Single.Source);
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
    }

    [Fact]
    public async Task Invalid_scale_is_rejected_before_playback()
    {
        using var fixture = AnimationFixture.Create([100], loop: "once", animationKey: "idle");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Engine.PlayAsync(TestPresentation("idle"), AnimationOptions.Default with { Scale = 0 }, TestContext.Current.CancellationToken));
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

        public static AnimationFixture Create(
            IReadOnlyList<int> durations,
            string loop,
            string animationKey,
            string packId = "fixture")
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-animation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "idle.png"), PngFixture.OnePixel);
            File.WriteAllBytes(Path.Combine(root, "celebrate.png"), PngFixture.OnePixel);

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
                },
            };
            var pack = new AssetPack(Path.Combine(root, "manifest.json"), manifest);
            var presenter = new RecordingFramePresenter();
            var clock = new ManualAnimationClock();
            var composer = new SkiaFrameComposer(pack);
            return new AnimationFixture(root, new AnimationEngine(pack, presenter, clock, composer), presenter, clock, composer, pack);
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

        public TimeSpan SemanticDuration => Frames.Count == 0 ? TimeSpan.Zero : Frames[0].SemanticDuration;

        public PresentedFrame Single => Assert.Single(Frames);

        public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(new PresentedFrame(frame.Source, frame.Opacity, frame.SemanticDuration));
            return ValueTask.CompletedTask;
        }
    }

    private readonly record struct PresentedFrame(string Source, float Opacity, TimeSpan SemanticDuration);

    private sealed class ManualAnimationClock : IAnimationClock
    {
        private readonly object _gate = new();
        private readonly List<(long Deadline, TaskCompletionSource<bool> Completion)> _waiters = [];
        private long _timestamp;

        public long Timestamp => Interlocked.Read(ref _timestamp);

        public long Frequency => 1000;

        public ValueTask DelayUntilAsync(long deadline, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
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
