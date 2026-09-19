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
        // Selects the sticker sentinel (index 6), then index 17 into the
        // restricted list — under the old (buggy) 1..30 roll this same random
        // value produces sticker-018 (index 17 -> number 18), which is one of
        // the two keys the pack is missing; under the fix, index 17 into the
        // filtered list is the 18th available key, sticker-019 (018 is
        // skipped). The test asserts the exact key so it fails under the old
        // behaviour instead of coincidentally passing either way, and
        // finally a value for the trailing next-eligible delay roll.
        var random = new SequenceRandomSource(6, 17, 0);
        var scheduler = new AmbientScheduler(clock, random, TimeSpan.Zero);

        var result = Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false,
            focusActive: false,
            fullscreen: false,
            sessionLocked: false,
            availableStickerKeys: availableStickerKeys));

        Assert.Equal("sticker-019", result.AnimationKey);
        Assert.DoesNotContain(result.AnimationKey, new[] { "sticker-018", "sticker-027" });
    }

    [Fact]
    public void A_pack_shipping_zero_stickers_falls_back_to_idle_instead_of_indexing_empty()
    {
        // Regression: SelectStickerKey treated an empty (non-null) list the same as
        // null and fell back to the legacy 1..30 roll -- but an empty list is
        // authoritative (the fallback pack legitimately ships zero stickers), not a
        // "no list given" signal. When the sticker sentinel is drawn there is nothing
        // to pick, so it must fall back to idle instead of indexing into the empty
        // list or rolling a number the pack has no art for at all.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        // Selects the sticker sentinel (index 6), then a draw consumed by the
        // empty-list branch to keep the random-source sequence the same length as
        // the non-empty/null branches, then the trailing next-eligible delay roll.
        var random = new SequenceRandomSource(6, 0, 0);
        var scheduler = new AmbientScheduler(clock, random, TimeSpan.Zero);

        var result = Assert.IsType<PetEvent.AmbientRequested>(scheduler.TryGetNextEvent(
            paused: false,
            focusActive: false,
            fullscreen: false,
            sessionLocked: false,
            availableStickerKeys: Array.Empty<string>()));

        Assert.Equal("idle", result.AnimationKey);
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
