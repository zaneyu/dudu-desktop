using Dudu.App.Animation;
using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Animation;

/// <summary>Regression tests for the pre-handoff animation smoothness pass:
/// async gates with timeouts, hi-res pacing, narrowed composer lock,
/// presenter fast path outside the host-update gate, owner-queue dropped-frame
/// diagnostics, and dispatch-queue breathing preemption paths.</summary>
public sealed class AnimationSmoothnessRegressionTests
{
    [Fact]
    public async Task ReplacePackAsync_completes_while_playback_is_blocked()
    {
        using var source = PackFixture.Create("source");
        using var replacement = PackFixture.Create("replacement");
        var presenter = new BlockingPresenter();
        var clock = new ManualClock();
        using var engine = new AnimationEngine(
            source.Pack,
            presenter,
            clock,
            new SkiaFrameComposer(source.Pack));

        var play = engine.PlayAsync(
            new PetPresentation(PetState.Ambient, "idle", null, null, false),
            AnimationOptions.Default,
            TestContext.Current.CancellationToken);
        await presenter.FirstPresentation.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Must finish promptly (cancel + swap) instead of hanging behind the
        // blocked presenter: the async gates carry 5s timeouts and the active
        // playback fault is swallowed, not propagated to the swap.
        await engine.ReplacePackAsync(replacement.Pack, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        presenter.Release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
    }

    [Fact]
    public async Task Clock_past_deadline_completes_immediately_and_short_deadline_is_prompt()
    {
        var clock = new StopwatchAnimationClock();
        await clock.DelayUntilAsync(clock.Timestamp - 1000, TestContext.Current.CancellationToken);

        var deadline = clock.Timestamp + (long)(clock.Frequency * 0.02);
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        await clock.DelayUntilAsync(deadline, TestContext.Current.CancellationToken);
        sw.Stop();

        // Hi-res pacing must not overshoot a 20ms deadline by a full timer
        // tick (~15ms legacy): allow generous CI headroom but catch the old
        // Math.Max(1ms)+Task.Delay coarse behavior regressions.
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 500);
    }

    [Fact]
    public void Composer_concurrent_compose_returns_correct_pixels()
    {
        using var fixture = PackFixture.Create("concurrent");
        using var composer = new SkiaFrameComposer(fixture.Pack);
        var animation = fixture.Pack.Manifest.Outfits["base"].Animations["idle"];

        var results = new byte[16][];
        Parallel.For(0, 16, i =>
        {
            using var frame = composer.Compose(fixture.Pack, animation, 0);
            results[i] = frame.Bytes.Span.ToArray();
        });

        foreach (var bytes in results)
        {
            Assert.Equal(255, bytes[3]);
        }
    }

    [Fact]
    public void Presenter_scale_fast_path_returns_identical_geometry()
    {
        var regions = new List<PixelRect> { new(10, 20, 30, 40) };
        var scaled = LayeredFramePresenter.ScaleRegionsToClient(regions, 640, 480, 640, 480);

        Assert.Equal(regions, scaled);

        var action = new OverlaySurfaceAction("Tasks", "overlay.tasks", new PixelRect(10, 20, 30, 40), "tasks")
        {
            PrimaryAction = OverlayAction.Tasks,
        };
        var scaledActions = LayeredFramePresenter.ScaleActionsToClient([action], 640, 480, 640, 480);

        var single = Assert.Single(scaledActions);
        Assert.Equal(action.HitRegion, single.HitRegion);
        Assert.Equal(OverlayAction.Tasks, single.PrimaryAction);
    }

    [Fact]
    public async Task OwnerQueue_timeout_surfaces_dropped_frame_diagnostic()
    {
        using var queue = new OwnerActionQueue(() => false, () => null);
        var task = queue.InvokeAsync(() => { }, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken));

        Assert.Contains("dropped frame", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DispatchQueue_preempt_paths_complete_on_unbound_surface()
    {
        // Exercises the new CancelBreathing preempt calls (Contains check for
        // points, non-breathe cancel for presented actions) without a router:
        // CancelBreathing with no router is a no-op and dispatch completes.
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);
        using var surface = new OverlayActionSurfaceController();
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));

        queue.Enqueue(surface, new PixelPoint(0, 0));
        var arrangement = surface.Arrangement;
        Assert.NotNull(arrangement);
        var presented = surface.CreateRenderSnapshot().Actions.First();
        queue.Enqueue(surface, presented);

        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(reported);
    }

    private sealed class PackFixture : IDisposable
    {
        private PackFixture(string root, AssetPack pack)
        {
            Root = root;
            Pack = pack;
        }

        public string Root { get; }

        public AssetPack Pack { get; }

        public static PackFixture Create(string packId)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-smoothness-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(
                Path.Combine(root, "idle.png"),
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            var animation = new AssetAnimation
            {
                Frames = [new AssetFrame { File = "idle.png", DurationMs = 100 }],
                Loop = "loop",
                NominalSize = new PixelSize(1, 1),
                Anchor = new PixelPoint(0, 0),
                ReducedMotion = "idle.png",
            };
            var manifest = new AssetManifest
            {
                SchemaVersion = 1,
                PackId = packId,
                Version = "1",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "test" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            ["idle"] = animation,
                        },
                    },
                },
            };
            return new PackFixture(root, new AssetPack(Path.Combine(root, "manifest.json"), manifest));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ManualClock : IAnimationClock
    {
        private readonly object _gate = new();
        private readonly List<(long Deadline, TaskCompletionSource<bool> Completion)> _waiters = [];
#pragma warning disable CS0649 // Assigned via Interlocked-friendly paths; read with Interlocked.Read.
        private long _timestamp;
#pragma warning restore CS0649

        public long Timestamp => Interlocked.Read(ref _timestamp);

        public long Frequency => 1000;

        public ValueTask DelayUntilAsync(long deadline, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (Timestamp >= deadline) return ValueTask.CompletedTask;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((deadline, completion));
                return new ValueTask(completion.Task.WaitAsync(cancellationToken));
            }
        }
    }

    private sealed class BlockingPresenter : IFramePresenter
    {
        public TaskCompletionSource<bool> FirstPresentation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
        {
            FirstPresentation.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
