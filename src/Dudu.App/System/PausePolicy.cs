namespace Dudu.App.System;

public enum PauseMode
{
    None,
    OneHour,
    FiveMinutes,
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
            PauseMode.OneHour or PauseMode.FiveMinutes or PauseMode.UntilTomorrowAtSeven =>
                state.ExpiresAtUtc is { } expiry && now < expiry,
            PauseMode.UntilFullscreenEnds => fullscreen,
            PauseMode.Indefinite => true,
            _ => false,
        };
    }

    public static PauseState ExpireIfNeeded(PauseState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Mode is PauseMode.OneHour or PauseMode.FiveMinutes or PauseMode.UntilTomorrowAtSeven
            && (state.ExpiresAtUtc is null || now >= state.ExpiresAtUtc)
            ? PauseState.None
            : state;
    }

    public static PauseState ForOneHour(DateTimeOffset now) =>
        new(PauseMode.OneHour, now.AddHours(1));

    public static PauseState ForFiveMinutes(DateTimeOffset now) =>
        new(PauseMode.FiveMinutes, now.AddMinutes(5));

    public static PauseState UntilTomorrowAtSeven(
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        // "tomorrow at 07:00" means the next 07:00: chosen at 01:00 it is
        // this morning's 07:00, not the following day's (which paused her for
        // about 30 hours).
        var resumeDate = localNow.TimeOfDay < TimeSpan.FromHours(7)
            ? localNow.Date
            : localNow.Date.AddDays(1);
        var localResume = DateTime.SpecifyKind(
            resumeDate.AddHours(7),
            DateTimeKind.Unspecified);
        var utcResume = TimeZoneInfo.ConvertTimeToUtc(localResume, timeZone);
        return new(
            PauseMode.UntilTomorrowAtSeven,
            new DateTimeOffset(utcResume, TimeSpan.Zero));
    }
}

/// <summary>
/// Maps a <see cref="PauseState"/> to and from the two plain preference
/// fields it is persisted in (<c>Preferences.PauseMode</c> /
/// <c>PauseExpiresUtc</c>), so a pause survives a restart instead of living
/// only in memory.
/// </summary>
public static class PausePersistence
{
    public static (string? Mode, DateTimeOffset? ExpiresUtc) ToPreference(PauseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Mode == PauseMode.None
            ? (null, null)
            : (state.Mode.ToString(), state.ExpiresAtUtc);
    }

    /// <summary>
    /// The pause to resume with at startup. An unknown, missing or expired
    /// value is no pause. "until fullscreen ends" is not restored: the
    /// fullscreen session it was tied to belonged to the previous run.
    /// </summary>
    public static PauseState FromPreference(string? mode, DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || !Enum.TryParse<PauseMode>(mode, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(parsed.ToString(), mode, StringComparison.Ordinal))
        {
            return PauseState.None;
        }

        return parsed switch
        {
            PauseMode.Indefinite => new PauseState(PauseMode.Indefinite, null),
            PauseMode.OneHour or PauseMode.FiveMinutes or PauseMode.UntilTomorrowAtSeven
                when expiresUtc is { } expiry =>
                PausePolicy.ExpireIfNeeded(new PauseState(parsed, expiry.ToUniversalTime()), now),
            _ => PauseState.None,
        };
    }
}
