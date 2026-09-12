namespace Dudu.App.System;

public enum PauseMode
{
    None,
    OneHour,
    UntilTomorrowAtSeven,
    UntilFullscreenEnds,
    Indefinite,
}

public sealed record PauseState(PauseMode Mode, DateTimeOffset? ExpiresAtUtc)
{
    public static PauseState None { get; } = new(PauseMode.None, null);
}

public static class PausePolicy
{
    public static bool IsSuppressed(
        PauseState state,
        DateTimeOffset now,
        bool fullscreen)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Mode switch
        {
            PauseMode.None => false,
            PauseMode.OneHour or PauseMode.UntilTomorrowAtSeven =>
                state.ExpiresAtUtc is { } expiry && now < expiry,
            PauseMode.UntilFullscreenEnds => fullscreen,
            PauseMode.Indefinite => true,
            _ => false,
        };
    }

    public static PauseState ExpireIfNeeded(PauseState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Mode is PauseMode.OneHour or PauseMode.UntilTomorrowAtSeven
            && (state.ExpiresAtUtc is null || now >= state.ExpiresAtUtc)
            ? PauseState.None
            : state;
    }

    public static PauseState ForOneHour(DateTimeOffset now) =>
        new(PauseMode.OneHour, now.AddHours(1));

    public static PauseState UntilTomorrowAtSeven(
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localResume = DateTime.SpecifyKind(
            localNow.Date.AddDays(1).AddHours(7),
            DateTimeKind.Unspecified);
        var utcResume = TimeZoneInfo.ConvertTimeToUtc(localResume, timeZone);
        return new(
            PauseMode.UntilTomorrowAtSeven,
            new DateTimeOffset(utcResume, TimeSpan.Zero));
    }
}
