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
        var now = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);

        var state = PausePolicy.UntilTomorrowAtSeven(now, zone);
        var localResume = TimeZoneInfo.ConvertTime(state.ExpiresAtUtc!.Value, zone);

        Assert.Equal(now.Date.AddDays(1), localResume.Date);
        Assert.Equal(new TimeOnly(7, 0), TimeOnly.FromDateTime(localResume.DateTime));
        Assert.False(PausePolicy.IsSuppressed(state, state.ExpiresAtUtc.Value, fullscreen: false));
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

    [Fact]
    public void Pause_persists_on_every_change_she_makes_but_not_on_restore_or_automatic_end()
    {
        // The pause used to live only in memory and was lost on restart.
        var persisted = new List<PauseState>();
        var fullscreen = false;
        var store = new PauseStateStore(() => fullscreen, persisted.Add);

        store.Restore(new PauseState(PauseMode.Indefinite, null));
        Assert.Empty(persisted);
        Assert.Equal(PauseMode.Indefinite, store.Current.Mode);

        var oneHour = PausePolicy.ForOneHour(Now);
        store.Set(oneHour);
        var untilFullscreenEnds = new PauseState(PauseMode.UntilFullscreenEnds, null);
        store.Set(untilFullscreenEnds);
        fullscreen = true;
        store.GetEffective(Now);
        fullscreen = false;
        Assert.Equal(PauseMode.None, store.GetEffective(Now).Mode);
        store.Set(PauseState.None);

        // The fullscreen-driven end is not persisted (a persisted
        // "until fullscreen ends" restores as not paused anyway).
        Assert.Equal([oneHour, untilFullscreenEnds, PauseState.None], persisted);
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
}
