using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Pet;

public sealed class PetActivitySchedulerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-26T10:00:00Z");

    [Fact]
    public void Fresh_scheduler_waits_for_the_first_activity_delay()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(clock, new FixedRandomSource(), AllMotionKeys());

        Assert.Equal(Start + PetActivityScheduler.FirstActivityDelay, scheduler.NextEligibleUtc);
        Assert.Null(scheduler.TryGetNext(PetActivityGate.Open));

        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        Assert.NotNull(scheduler.TryGetNext(PetActivityGate.Open));
    }

    [Fact]
    public void Activities_are_spaced_by_the_minimum_interval_plus_jitter()
    {
        var clock = new FakeClock(Start);
        // wander roll 99 (fidget), fidget index 0, jitter 7s.
        var scheduler = new PetActivityScheduler(clock, new SequenceRandomSource(99, 0, 7), AllMotionKeys());
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        Assert.NotNull(scheduler.TryGetNext(PetActivityGate.Open));

        Assert.Equal(
            clock.UtcNow + PetActivityScheduler.MinimumInterval + TimeSpan.FromSeconds(7),
            scheduler.NextEligibleUtc);
        Assert.Null(scheduler.TryGetNext(PetActivityGate.Open));
    }

    [Fact]
    public void A_low_roll_wanders_with_the_walk_clip_in_the_rolled_direction_and_distance()
    {
        var clock = new FakeClock(Start);
        // wander roll 0 (< 40), direction roll 1 (right), distance roll 20, jitter 0.
        var scheduler = new PetActivityScheduler(clock, new SequenceRandomSource(0, 1, 20, 0), AllMotionKeys());
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        var activity = scheduler.TryGetNext(PetActivityGate.Open);

        Assert.NotNull(activity);
        Assert.Equal(PetActivityKind.Wander, activity.Kind);
        Assert.Equal(AssetManifestContract.WalkAnimationKey, activity.AnimationKey);
        Assert.Equal(1, activity.Direction);
        Assert.Equal(PetActivityScheduler.MinimumWanderDistance + 20, activity.Distance);
    }

    [Fact]
    public void A_high_roll_fidgets_with_a_motion_clip_and_never_the_walk_clip()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(clock, new SequenceRandomSource(40, 1, 0), AllMotionKeys());
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        var activity = scheduler.TryGetNext(PetActivityGate.Open);

        Assert.NotNull(activity);
        Assert.Equal(PetActivityKind.Fidget, activity.Kind);
        Assert.Equal(scheduler.FidgetKeys[1], activity.AnimationKey);
        Assert.DoesNotContain(AssetManifestContract.WalkAnimationKey, scheduler.FidgetKeys);
        Assert.True(AssetManifestContract.IsMotionAnimationKey(activity.AnimationKey));
    }

    [Fact]
    public void The_same_fidget_never_plays_twice_in_a_row()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(
            clock,
            new SequenceRandomSource(99, 2, 0, 99, 2, 0),
            AllMotionKeys());
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        var first = scheduler.TryGetNext(PetActivityGate.Open);
        clock.Advance(PetActivityScheduler.MinimumInterval);
        var second = scheduler.TryGetNext(PetActivityGate.Open);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.AnimationKey, second.AnimationKey);
    }

    [Fact]
    public void Only_clips_the_pack_ships_are_chosen()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(
            clock,
            new FixedRandomSource(),
            ["idle", "blink", "hop", "sticker-004"]);
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        Assert.False(scheduler.CanWander);
        Assert.Equal(new[] { "hop" }, scheduler.FidgetKeys);
        var activity = scheduler.TryGetNext(PetActivityGate.Open);

        Assert.NotNull(activity);
        Assert.Equal(PetActivity.Fidget("hop"), activity);
    }

    [Fact]
    public void A_pack_with_only_the_walk_clip_always_wanders()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(
            clock,
            new SequenceRandomSource(0, 0, 0),
            ["idle", AssetManifestContract.WalkAnimationKey]);
        clock.Advance(PetActivityScheduler.FirstActivityDelay);

        var activity = scheduler.TryGetNext(PetActivityGate.Open);

        Assert.NotNull(activity);
        Assert.Equal(PetActivityKind.Wander, activity.Kind);
        Assert.Equal(-1, activity.Direction);
        Assert.Equal(PetActivityScheduler.MinimumWanderDistance, activity.Distance);
    }

    [Fact]
    public void A_pack_without_motion_clips_stays_still()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(
            clock,
            new ThrowingRandomSource(),
            AssetManifestContract.RequiredAnimationKeys);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(scheduler.HasActivities);
        Assert.Null(scheduler.TryGetNext(PetActivityGate.Open));
    }

    public static TheoryData<string> SuppressingGates() =>
    [
        nameof(PetActivityGate.ReducedMotion),
        nameof(PetActivityGate.Paused),
        nameof(PetActivityGate.Fullscreen),
        nameof(PetActivityGate.SessionLocked),
        nameof(PetActivityGate.Hidden),
        nameof(PetActivityGate.QuietHours),
        nameof(PetActivityGate.Busy),
    ];

    [Theory]
    [MemberData(nameof(SuppressingGates))]
    public void Any_suppressing_gate_keeps_dudu_still_and_settles_before_the_next_activity(string gateName)
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(clock, new FixedRandomSource(), AllMotionKeys());
        clock.Advance(PetActivityScheduler.FirstActivityDelay);
        var gate = gateName switch
        {
            nameof(PetActivityGate.ReducedMotion) => PetActivityGate.Open with { ReducedMotion = true },
            nameof(PetActivityGate.Paused) => PetActivityGate.Open with { Paused = true },
            nameof(PetActivityGate.Fullscreen) => PetActivityGate.Open with { Fullscreen = true },
            nameof(PetActivityGate.SessionLocked) => PetActivityGate.Open with { SessionLocked = true },
            nameof(PetActivityGate.Hidden) => PetActivityGate.Open with { Hidden = true },
            nameof(PetActivityGate.QuietHours) => PetActivityGate.Open with { QuietHours = true },
            _ => PetActivityGate.Open with { Busy = true },
        };

        Assert.True(gate.IsSuppressed);
        Assert.Null(scheduler.TryGetNext(gate));
        Assert.Equal(clock.UtcNow + PetActivityScheduler.MinimumInterval, scheduler.NextEligibleUtc);

        // The gate reopening does not trigger motion the same instant.
        Assert.Null(scheduler.TryGetNext(PetActivityGate.Open));
        clock.Advance(PetActivityScheduler.MinimumInterval);
        Assert.NotNull(scheduler.TryGetNext(PetActivityGate.Open));
    }

    [Fact]
    public void Suppression_never_pulls_a_later_activity_earlier()
    {
        var clock = new FakeClock(Start);
        var scheduler = new PetActivityScheduler(clock, new FixedRandomSource(), AllMotionKeys());
        var scheduled = scheduler.NextEligibleUtc;

        Assert.Null(scheduler.TryGetNext(PetActivityGate.Open with { Busy = true }));

        Assert.Equal(scheduled, scheduler.NextEligibleUtc);
    }

    [Fact]
    public void Every_motion_key_is_a_supported_one_shot_animation()
    {
        foreach (var key in AssetManifestContract.MotionAnimationKeys)
        {
            Assert.True(AssetManifestContract.IsSupportedAnimationKey(key), key);
            Assert.True(AssetManifestContract.IsOneShotAnimationKey(key), key);
            Assert.DoesNotContain(key, AssetManifestContract.RequiredAnimationKeys);
        }

        Assert.False(AssetManifestContract.IsMotionAnimationKey("sticker-004"));
        Assert.False(AssetManifestContract.IsMotionAnimationKey(null));
    }

    private static string[] AllMotionKeys() =>
        [.. AssetManifestContract.RequiredAnimationKeys, .. AssetManifestContract.MotionAnimationKeys];

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    private sealed class ThrowingRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => throw new InvalidOperationException("No random draw expected.");
    }

    private sealed class SequenceRandomSource(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int exclusiveMax) => _values.Dequeue();
    }
}
