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
}
