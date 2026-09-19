using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
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
    public void A_pack_missing_some_sticker_numbers_never_yields_a_missing_key()
    {
        // Regression: the scheduler used to roll uniformly over the full
        // 1..StickerAnimationCount range regardless of which sticker keys
        // the active pack actually ships. A number the pack has no art for
        // (e.g. the shipped pack currently omits sticker-018/027) resolves
        // silently to the base idle loop instead of a sticker. Restricting
        // the roll to the pack's real keys must never select a missing one.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var availableStickerKeys = Enumerable.Range(1, AssetManifestContract.StickerAnimationCount)
            .Select(AssetManifestContract.StickerAnimationKey)
            .Where(key => key is not ("sticker-018" or "sticker-027"))
            .ToArray();
        // Selects the sticker sentinel (index 6), then the last index into
        // the restricted list — without the fix this would still be
        // interpreted as a 1..30 roll and could land on a missing key — and
        // finally a value for the trailing next-eligible delay roll.
        var random = new SequenceRandomSource(6, availableStickerKeys.Length - 1, 0);
        var scheduler = new AmbientScheduler(clock, random, TimeSpan.Zero);

        var result = Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false,
            focusActive: false,
            fullscreen: false,
            sessionLocked: false,
            availableStickerKeys: availableStickerKeys));

        Assert.Contains(result.AnimationKey, availableStickerKeys);
        Assert.DoesNotContain(result.AnimationKey, new[] { "sticker-018", "sticker-027" });
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
