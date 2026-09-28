using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

/// <summary>
/// Pins how every persisted <see cref="PauseMode"/> restores at startup. The
/// tray now only offers "pause 1 hour" / "resume dudu", but an install that
/// upgrades with a pause saved by the older, longer menu (five minutes,
/// until tomorrow at 07:00, until fullscreen ends, until i resume) must
/// restore exactly as it did before the menu shrank.
/// </summary>
public sealed class PauseLegacyRestoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T10:00:00Z");

    [Fact]
    public void Every_legacy_mode_restores_exactly_as_before()
    {
        foreach (var mode in Enum.GetValues<PauseMode>())
        {
            var original = mode switch
            {
                PauseMode.None => PauseState.None,
                _ => new PauseState(
                    mode,
                    mode is PauseMode.Indefinite or PauseMode.UntilFullscreenEnds ? null : Now.AddHours(1)),
            };
            var (persistedMode, expires) = PausePersistence.ToPreference(original);
            var restored = PausePersistence.FromPreference(persistedMode, expires, Now);

            // "until fullscreen ends" belonged to the previous run's fullscreen
            // session, so it restores as not paused; every other mode round-trips.
            var expected = mode == PauseMode.UntilFullscreenEnds ? PauseState.None : original;
            Assert.Equal(expected, restored);
        }
    }

    [Theory]
    [InlineData("OneHour")]
    [InlineData("FiveMinutes")]
    [InlineData("UntilTomorrowAtSeven")]
    public void An_expired_timed_legacy_pause_restores_as_not_paused(string mode)
    {
        Assert.Equal(
            PauseState.None,
            PausePersistence.FromPreference(mode, Now.AddMinutes(-1), Now));
    }

    [Theory]
    [InlineData("OneHour")]
    [InlineData("FiveMinutes")]
    [InlineData("UntilTomorrowAtSeven")]
    public void A_timed_legacy_pause_without_an_expiry_restores_as_not_paused(string mode)
    {
        Assert.Equal(PauseState.None, PausePersistence.FromPreference(mode, null, Now));
    }

    [Fact]
    public void Persisted_mode_names_are_unchanged()
    {
        // The names are stored in preferences.pause_mode; renaming a member
        // would silently drop an existing install's pause.
        Assert.Equal(
            ["None", "OneHour", "FiveMinutes", "UntilTomorrowAtSeven", "UntilFullscreenEnds", "Indefinite"],
            Enum.GetNames<PauseMode>());
    }
}
