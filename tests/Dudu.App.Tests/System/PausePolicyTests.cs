using Dudu.App.System;
using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class PausePolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PauseMode.None, false, false)]
    [InlineData(PauseMode.OneHour, true, true)]
    [InlineData(PauseMode.UntilFullscreenEnds, true, true)]
    [InlineData(PauseMode.UntilFullscreenEnds, false, false)]
    [InlineData(PauseMode.Indefinite, false, true)]
    public void Pause_policy_combines_mode_expiry_and_fullscreen(
        PauseMode mode,
        bool fullscreen,
        bool expected)
    {
        var state = new PauseState(mode, Now.AddMinutes(30));

        Assert.Equal(expected, PausePolicy.IsSuppressed(state, Now, fullscreen));
    }

    [Fact]
    public void Expired_pause_is_not_suppressed_and_normalizes_to_none()
    {
        var state = new PauseState(PauseMode.OneHour, Now.AddMinutes(-1));

        Assert.False(PausePolicy.IsSuppressed(state, Now, fullscreen: true));
        Assert.Equal(PauseMode.None, PausePolicy.ExpireIfNeeded(state, Now).Mode);
    }

    [Fact]
    public void Tomorrow_at_seven_uses_local_date_and_dst_aware_offset()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
        // 01:30 PDT on the night DST ends: the next 07:00 is this same local
        // morning (07:00 PST, after the fall-back) -- not the day after,
        // which used to pause her for about 30 hours.
        var now = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        Assert.True(localNow.Hour < 7);

        var state = PausePolicy.UntilTomorrowAtSeven(now, zone);
        var localResume = TimeZoneInfo.ConvertTime(state.ExpiresAtUtc!.Value, zone);

        Assert.Equal(localNow.Date, localResume.Date);
        Assert.Equal(new TimeOnly(7, 0), TimeOnly.FromDateTime(localResume.DateTime));
        Assert.Equal(TimeSpan.FromHours(-8), localResume.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), state.ExpiresAtUtc);
        Assert.False(PausePolicy.IsSuppressed(state, state.ExpiresAtUtc.Value, fullscreen: false));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(6, 59, 0)]
    [InlineData(7, 0, 1)]
    [InlineData(23, 30, 1)]
    public void Tomorrow_at_seven_resumes_at_the_next_local_seven_oclock(int hour, int minute, int expectedDayOffset)
    {
        var now = new DateTimeOffset(2026, 9, 11, hour, minute, 0, TimeSpan.Zero);

        var state = PausePolicy.UntilTomorrowAtSeven(now, TimeZoneInfo.Utc);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 11 + expectedDayOffset, 7, 0, 0, TimeSpan.Zero),
            state.ExpiresAtUtc);
        Assert.True(state.ExpiresAtUtc > now);
        Assert.True(state.ExpiresAtUtc - now <= TimeSpan.FromHours(24));
    }

    [Fact]
    public void Pause_persists_on_every_change_she_makes_but_not_on_restore()
    {
        // The pause used to live only in memory and was lost on restart.
        var persisted = new List<PauseState>();
        var store = new PauseStateStore(persist: persisted.Add);

        store.Restore(new PauseState(PauseMode.Indefinite, null));
        Assert.Empty(persisted);
        Assert.Equal(PauseMode.Indefinite, store.Current.Mode);

        var oneHour = PausePolicy.ForOneHour(Now);
        store.Set(oneHour);
        store.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));
        store.OnFullscreenChanged(true);
        store.OnFullscreenChanged(false);

        Assert.Equal(
            [oneHour, new PauseState(PauseMode.UntilFullscreenEnds, null), PauseState.None],
            persisted);
    }

    [Fact]
    public void A_throwing_persist_callback_never_breaks_the_pause()
    {
        var store = new PauseStateStore(persist: _ => throw new InvalidOperationException("disk full"));

        store.Set(new PauseState(PauseMode.Indefinite, null));

        Assert.Equal(PauseMode.Indefinite, store.Current.Mode);
    }

    [Fact]
    public void Persisted_pause_round_trips_through_preference_fields()
    {
        foreach (var state in new[]
        {
            new PauseState(PauseMode.Indefinite, null),
            PausePolicy.ForOneHour(Now),
            PausePolicy.ForFiveMinutes(Now),
            PausePolicy.UntilTomorrowAtSeven(Now, TimeZoneInfo.Utc),
        })
        {
            var (mode, expires) = PausePersistence.ToPreference(state);
            Assert.Equal(state, PausePersistence.FromPreference(mode, expires, Now));
        }

        Assert.Equal(((string?)null, (DateTimeOffset?)null), PausePersistence.ToPreference(PauseState.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("None", null)]
    [InlineData("oneHour", "2026-09-11T11:00:00Z")]
    [InlineData("3", "2026-09-11T11:00:00Z")]
    [InlineData("NotAMode", null)]
    [InlineData("OneHour", null)]
    [InlineData("OneHour", "2026-09-11T09:59:00Z")]
    [InlineData("UntilFullscreenEnds", null)]
    public void Unknown_expired_or_fullscreen_bound_persisted_pauses_restore_as_not_paused(
        string? mode,
        string? expiresUtc)
    {
        var expires = expiresUtc is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(expiresUtc);

        Assert.Equal(PauseState.None, PausePersistence.FromPreference(mode, expires, Now));
    }

    [Fact]
    public void Pause_until_fullscreen_ends_clears_once_a_fullscreen_session_ends()
    {
        var store = new PauseStateStore();
        store.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));

        // Not-fullscreen before any fullscreen session began does not end it.
        Assert.False(store.OnFullscreenChanged(false));
        Assert.Equal(PauseMode.UntilFullscreenEnds, store.Current.Mode);
        Assert.False(store.OnFullscreenChanged(true));
        Assert.Equal(PauseMode.UntilFullscreenEnds, store.Current.Mode);

        Assert.True(store.OnFullscreenChanged(false));
        Assert.Equal(PauseMode.None, store.Current.Mode);
        Assert.Equal(PauseMode.None, store.GetEffective(Now).Mode);
    }

    [Fact]
    public void Pause_until_fullscreen_ends_chosen_during_fullscreen_ends_with_that_session()
    {
        var fullscreen = true;
        var store = new PauseStateStore(() => fullscreen);
        store.Set(new PauseState(PauseMode.UntilFullscreenEnds, null));

        fullscreen = false;
        Assert.True(store.OnFullscreenChanged(false));
        Assert.Equal(PauseMode.None, store.Current.Mode);
    }

    [Theory]
    [InlineData(PauseMode.Indefinite)]
    [InlineData(PauseMode.OneHour)]
    public void Fullscreen_transitions_never_clear_other_pause_modes(PauseMode mode)
    {
        var store = new PauseStateStore();
        var state = new PauseState(mode, mode == PauseMode.OneHour ? Now.AddHours(1) : null);
        store.Set(state);

        Assert.False(store.OnFullscreenChanged(true));
        Assert.False(store.OnFullscreenChanged(false));
        Assert.Equal(state, store.Current);
    }

    [Fact]
    public void Pause_state_store_clears_expired_suppression_when_read()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var store = new PauseStateStore();
        store.Set(PausePolicy.ForOneHour(now.AddHours(-2)));

        Assert.Equal(PauseMode.None, store.GetEffective(now).Mode);
        Assert.Equal(PauseMode.None, store.Current.Mode);
    }
}
