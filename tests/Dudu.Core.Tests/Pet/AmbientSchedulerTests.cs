using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Pet;

public sealed class AmbientSchedulerTests
{
    [Fact]
    public void Fresh_scheduler_holds_its_first_moment_for_one_minimum_interval()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var interval = TimeSpan.FromMinutes(15);

        var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), interval);

        Assert.Equal(clock.UtcNow + interval, scheduler.NextEligibleUtc);
        Assert.Null(scheduler.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false));
    }

    [Fact]
    public void Scheduler_fires_once_the_minimum_interval_elapses()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false));
    }

    [Fact]
    public void Scheduler_only_requests_animations_backed_by_the_private_pack_contract()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var random = new SequenceRandomSource(2, 0);
        var scheduler = new AmbientScheduler(clock, random, TimeSpan.Zero);

        var result = Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false));

        Assert.Contains(result.AnimationKey, new[]
        {
            "idle", "blink", "greeting", "sleep", "drink", "celebrate",
        });
    }

    [Fact]
    public void Scheduler_can_request_one_random_sticker_animation()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var random = new SequenceRandomSource(6, 29, 0);
        var scheduler = new AmbientScheduler(clock, random, TimeSpan.Zero);

        var result = Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false));

        Assert.Equal("sticker-030", result.AnimationKey);
    }

    [Fact]
    public void Explicit_quiet_override_wins_over_the_policy_in_both_directions()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T23:00:00Z"));
        var quiet = new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0));

        var policyDriven = new AmbientScheduler(clock, new FixedRandomSource(), TimeSpan.Zero, quiet);
        Assert.Null(policyDriven.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false));

        var overridden = new AmbientScheduler(clock, new FixedRandomSource(), TimeSpan.Zero, quiet);
        Assert.IsType<PetEvent.AmbientRequested>(overridden.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false,
            quietHoursOverride: false));

        var forcedQuiet = new AmbientScheduler(clock, new FixedRandomSource(), TimeSpan.Zero, quiet with { Enabled = false });
        Assert.Null(forcedQuiet.TryGetNextEvent(
            paused: false, focusActive: false, fullscreen: false, sessionLocked: false,
            quietHoursOverride: true));
    }

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

    private sealed class SequenceRandomSource(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int exclusiveMax) => _values.Dequeue();
    }
}
