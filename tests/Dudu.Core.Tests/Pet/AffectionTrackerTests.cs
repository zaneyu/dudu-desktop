using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Pet;

public sealed class AffectionTrackerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    [Fact]
    public void No_tantrum_before_ninety_active_minutes_then_one_is_due()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);

        Assert.False(RunActive(tracker, clock, AffectionTracker.NeglectThreshold - Tick));
        clock.Advance(Tick);

        Assert.True(tracker.Observe(active: true));
    }

    [Fact]
    public void Tantrum_stays_due_until_recorded_so_a_busy_tick_only_defers_it()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);
        RunActive(tracker, clock, AffectionTracker.NeglectThreshold);

        Assert.True(tracker.Observe(active: true));
        clock.Advance(Tick);
        Assert.True(tracker.Observe(active: true));

        tracker.RecordTantrum();
        clock.Advance(Tick);
        Assert.False(tracker.Observe(active: true));
    }

    [Fact]
    public void At_most_one_tantrum_per_cooldown_while_still_neglected()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);
        RunActive(tracker, clock, AffectionTracker.NeglectThreshold);
        Assert.True(tracker.Observe(active: true));
        tracker.RecordTantrum();

        Assert.False(RunActive(tracker, clock, AffectionTracker.TantrumCooldown - Tick));
        clock.Advance(Tick);

        Assert.True(tracker.Observe(active: true));
    }

    [Fact]
    public void Petting_resets_the_neglect_clock_and_the_cooldown()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);
        RunActive(tracker, clock, AffectionTracker.NeglectThreshold);
        tracker.RecordTantrum();

        Assert.Equal(PetReaction.Petted, tracker.RecordPet());

        Assert.Equal(TimeSpan.Zero, tracker.NeglectedFor);
        Assert.False(RunActive(tracker, clock, AffectionTracker.NeglectThreshold - Tick));
        clock.Advance(Tick);
        Assert.True(tracker.Observe(active: true));
    }

    [Fact]
    public void Inactive_time_and_long_gaps_do_not_count_as_neglect()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);
        RunActive(tracker, clock, TimeSpan.FromMinutes(60));
        var afterHour = tracker.NeglectedFor;

        // Locked / quiet hours / focus: observed but inactive.
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(3); elapsed += Tick)
        {
            clock.Advance(Tick);
            Assert.False(tracker.Observe(active: false));
        }

        // Asleep overnight: no observations at all, then back.
        clock.Advance(TimeSpan.FromHours(8));
        Assert.False(tracker.Observe(active: true));

        Assert.Equal(afterHour, tracker.NeglectedFor);
    }

    [Fact]
    public void Inactive_observation_never_reports_a_due_tantrum()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);
        RunActive(tracker, clock, AffectionTracker.NeglectThreshold);

        clock.Advance(Tick);

        Assert.False(tracker.Observe(active: false));
    }

    [Fact]
    public void Three_quick_pets_delight_him_and_the_streak_restarts()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);

        Assert.Equal(PetReaction.Petted, tracker.RecordPet());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PetReaction.Petted, tracker.RecordPet());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PetReaction.Delighted, tracker.RecordPet());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(PetReaction.Petted, tracker.RecordPet());
    }

    [Fact]
    public void Slow_pets_never_delight()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var tracker = new AffectionTracker(clock);

        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(PetReaction.Petted, tracker.RecordPet());
            clock.Advance(TimeSpan.FromSeconds(31));
        }
    }

    private static bool RunActive(AffectionTracker tracker, FakeClock clock, TimeSpan duration)
    {
        var anyDue = tracker.Observe(active: true);
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Tick)
        {
            clock.Advance(Tick);
            anyDue |= tracker.Observe(active: true);
        }

        return anyDue;
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
