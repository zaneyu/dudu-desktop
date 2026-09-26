using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.Presentation;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PetActivityDirectorTests
{
    private static readonly OverlayWanderPlan Route = new(
        new PixelRect(800, 600, 384, 384),
        new PixelRect(950, 600, 384, 384));

    [Fact]
    public async Task A_due_fidget_plays_its_clip_through_the_idle_one_shot_path()
    {
        // fidget roll, fidget index 1, jitter.
        var harness = new Harness(99, 1, 0);

        var activity = await harness.Director.TickAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.Equal(PetActivityKind.Fidget, activity.Kind);
        var call = Assert.Single(harness.Presented);
        Assert.Equal(new PetEvent.AmbientRequested(activity.AnimationKey), call.Event);
        Assert.Equal(activity.AnimationKey, call.DismissalId);
        Assert.Null(call.Companion);
        Assert.Empty(harness.Plans);
    }

    [Fact]
    public async Task A_due_wander_plans_a_route_and_glides_it_while_the_walk_clip_plays()
    {
        // wander roll, direction right, distance +20, jitter.
        var harness = new Harness(0, 1, 20, 0) { Plan = Route };

        var activity = await harness.Director.TickAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.Equal(PetActivityKind.Wander, activity.Kind);
        Assert.Equal((1, PetActivityScheduler.MinimumWanderDistance + 20), Assert.Single(harness.Plans));
        var call = Assert.Single(harness.Presented);
        Assert.Equal(new PetEvent.AmbientRequested(AssetManifestContract.WalkAnimationKey), call.Event);
        Assert.NotNull(call.Companion);
        var glide = Assert.Single(harness.Glides);
        Assert.Equal(Route, glide.Plan);
        Assert.Equal(harness.Director.WanderDuration, glide.Duration);
    }

    [Fact]
    public async Task No_room_to_walk_means_no_walk_clip_either()
    {
        var harness = new Harness(0, 0, 0, 0) { Plan = null };

        var activity = await harness.Director.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(activity);
        Assert.Single(harness.Plans);
        Assert.Empty(harness.Presented);
    }

    [Fact]
    public async Task A_suppressed_gate_plays_nothing()
    {
        var harness = new Harness(99, 0, 0) { Gate = PetActivityGate.Open with { ReducedMotion = true } };

        Assert.Null(await harness.Director.TickAsync(TestContext.Current.CancellationToken));

        Assert.Empty(harness.Presented);
        Assert.Empty(harness.Plans);
    }

    [Fact]
    public async Task A_busy_pet_reports_nothing_played()
    {
        var harness = new Harness(99, 0, 0) { PetIsIdle = false };

        Assert.Null(await harness.Director.TickAsync(TestContext.Current.CancellationToken));

        Assert.Single(harness.Presented);
    }

    [Fact]
    public async Task Failures_are_reported_under_the_pet_activity_operation_and_never_thrown()
    {
        var harness = new Harness(99, 0, 0) { PresentFailure = new InvalidOperationException("boom") };

        Assert.Null(await harness.Director.TickAsync(TestContext.Current.CancellationToken));

        var report = Assert.Single(harness.Reporter.Reports);
        Assert.Equal(PetActivityDirector.ActivityOperation, report.Operation);
        Assert.IsType<InvalidOperationException>(report.Exception);
    }

    [Fact]
    public async Task A_gate_read_failure_is_reported_and_skips_the_tick()
    {
        var harness = new Harness(99, 0, 0) { GateFailure = new InvalidOperationException("gate") };

        Assert.Null(await harness.Director.TickAsync(TestContext.Current.CancellationToken));

        Assert.Single(harness.Reporter.Reports);
        Assert.Empty(harness.Presented);
    }

    [Fact]
    public async Task Started_loop_ticks_until_disposed()
    {
        var harness = new Harness(99, 0, 0, 99, 1, 0, 99, 2, 0, 99, 3, 0);
        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnPresented = () => ticked.TrySetResult();
        var director = harness.CreateDirector(delayAsync: (_, token) => Task.Delay(1, token));

        director.Start(TestContext.Current.CancellationToken);
        await ticked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await director.DisposeAsync();

        Assert.NotEmpty(harness.Presented);
        Assert.Empty(harness.Reporter.Reports);
    }

    [Fact]
    public async Task A_pack_without_motion_clips_never_starts_a_loop()
    {
        var harness = new Harness(availableKeys: AssetManifestContract.RequiredAnimationKeys);
        var delays = 0;
        var director = harness.CreateDirector(delayAsync: (_, _) =>
        {
            Interlocked.Increment(ref delays);
            return Task.CompletedTask;
        });

        director.Start(TestContext.Current.CancellationToken);
        await director.DisposeAsync();

        Assert.Equal(0, delays);
    }

    [Fact]
    public void Wander_duration_follows_the_walk_clip_within_the_one_shot_cap()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2400), PetActivityDirector.WanderDurationFor(Pack(("walk", 16, 150))));
        Assert.Equal(PetActivityDirector.MaximumWanderDuration, PetActivityDirector.WanderDurationFor(Pack(("walk", 20, 500))));
        Assert.Equal(PetActivityDirector.DefaultWanderDuration, PetActivityDirector.WanderDurationFor(Pack(("hop", 4, 100))));
        Assert.Equal(new[] { "hop" }, PetActivityDirector.AvailableAnimationKeys(Pack(("hop", 4, 100))));
    }

    private static AssetPack Pack((string Key, int Frames, int DurationMs) animation) =>
        new(
            Path.Combine(Path.GetTempPath(), "dudu-activity-pack", "manifest.json"),
            new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture",
                Version = "1.0.0",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "fixture" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            [animation.Key] = new AssetAnimation
                            {
                                Frames = Enumerable.Range(0, animation.Frames)
                                    .Select(_ => new AssetFrame { File = "pose.png", DurationMs = animation.DurationMs })
                                    .ToList(),
                                Loop = "once",
                            },
                        },
                    },
                },
            });

    private sealed record PresentCall(PetEvent Event, string DismissalId, Func<CancellationToken, Task>? Companion);

    private sealed class Harness
    {
        private readonly PetActivityScheduler _scheduler;

        public Harness(params int[] randomValues)
            : this(null, randomValues)
        {
        }

        public Harness(IEnumerable<string>? availableKeys, params int[] randomValues)
        {
            var clock = new FakeClock(DateTimeOffset.Parse("2026-09-26T10:00:00Z"));
            _scheduler = new PetActivityScheduler(
                clock,
                new SequenceRandomSource(randomValues),
                availableKeys ?? [.. AssetManifestContract.RequiredAnimationKeys, .. AssetManifestContract.MotionAnimationKeys]);
            clock.Advance(PetActivityScheduler.FirstActivityDelay);
            Director = CreateDirector();
        }

        public PetActivityDirector Director { get; }

        public RecordingErrorReporter Reporter { get; } = new();

        public PetActivityGate Gate { get; init; } = PetActivityGate.Open;

        public Exception? GateFailure { get; init; }

        public Exception? PresentFailure { get; init; }

        public bool PetIsIdle { get; init; } = true;

        public OverlayWanderPlan? Plan { get; init; }

        public Action? OnPresented { get; set; }

        public List<PresentCall> Presented { get; } = [];

        public List<(int Direction, int Distance)> Plans { get; } = [];

        public List<(OverlayWanderPlan Plan, TimeSpan Duration)> Glides { get; } = [];

        public PetActivityDirector CreateDirector(Func<TimeSpan, CancellationToken, Task>? delayAsync = null) =>
            new(
                _scheduler,
                () => GateFailure is null ? Gate : throw GateFailure,
                async (petEvent, dismissalId, companion, token) =>
                {
                    lock (Presented)
                    {
                        Presented.Add(new PresentCall(petEvent, dismissalId, companion));
                    }

                    OnPresented?.Invoke();
                    if (PresentFailure is not null) throw PresentFailure;
                    if (!PetIsIdle) return false;
                    if (companion is not null) await companion(token);
                    return true;
                },
                (direction, distance, _) =>
                {
                    Plans.Add((direction, distance));
                    return Task.FromResult(Plan);
                },
                (plan, duration, _) =>
                {
                    Glides.Add((plan, duration));
                    return Task.FromResult(true);
                },
                wanderDuration: TimeSpan.FromMilliseconds(2400),
                errorReporter: Reporter,
                delayAsync: delayAsync,
                tickInterval: TimeSpan.FromMilliseconds(1));
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = [];

        public void Report(string operation, Exception exception)
        {
            lock (Reports)
            {
                Reports.Add((operation, exception));
            }
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class SequenceRandomSource(int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int exclusiveMax) => _values.Count == 0 ? 0 : _values.Dequeue();
    }
}
