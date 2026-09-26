using System.Globalization;

namespace Dudu.Core.Time;

/// <summary>
/// The long-distance partner's wall clock. The partner zone is fixed to the UK
/// (<see cref="PartnerZoneId"/>); change that one constant (and the two
/// abbreviations beside it) to follow someone else. Pure and clock-injected:
/// every display string is derived from <see cref="IClock.UtcNow"/> and
/// <see cref="IClock.LocalTimeZone"/>, so it is deterministic under test and
/// needs no settings or database state.
/// </summary>
public sealed class PartnerClock
{
    /// <summary>IANA id of the partner's time zone. The single source of truth.</summary>
    public const string PartnerZoneId = "Europe/London";

    /// <summary>Windows registry id for <see cref="PartnerZoneId"/>, used when the
    /// runtime cannot resolve IANA ids (no ICU).</summary>
    public const string PartnerZoneWindowsId = "GMT Standard Time";

    /// <summary>Short place label used on the overlay pill ("UK 14:05").</summary>
    public const string PartnerPlaceLabel = "UK";

    public const string StandardAbbreviation = "GMT";
    public const string DaylightAbbreviation = "BST";

    private readonly IClock _clock;

    public PartnerClock(IClock clock, TimeZoneInfo? partnerZone = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PartnerZone = partnerZone ?? ResolvePartnerZone();
    }

    public TimeZoneInfo PartnerZone { get; }

    public PartnerClockReading Now() => Read(_clock.UtcNow, PartnerZone, _clock.LocalTimeZone);

    public static PartnerClockReading Read(
        DateTimeOffset utcNow,
        TimeZoneInfo partnerZone,
        TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(partnerZone);
        ArgumentNullException.ThrowIfNull(localZone);
        var utc = utcNow.ToUniversalTime();
        var partner = TimeZoneInfo.ConvertTime(utc, partnerZone);
        var local = TimeZoneInfo.ConvertTime(utc, localZone);
        return new PartnerClockReading(partner, local, partner.Offset > partnerZone.BaseUtcOffset);
    }

    /// <summary>Resolves the partner zone from the OS: IANA id first, then the
    /// Windows id, then a built-in rule-based UK zone so the clock never fails.</summary>
    public static TimeZoneInfo ResolvePartnerZone() => ResolvePartnerZone(TimeZoneInfo.FindSystemTimeZoneById);

    public static TimeZoneInfo ResolvePartnerZone(Func<string, TimeZoneInfo> findSystemZone)
    {
        ArgumentNullException.ThrowIfNull(findSystemZone);
        foreach (var id in new[] { PartnerZoneId, PartnerZoneWindowsId })
        {
            try
            {
                return findSystemZone(id);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException
                or InvalidTimeZoneException
                or global::System.Security.SecurityException)
            {
                // Try the next id, then the built-in rules.
            }
        }

        return CreateFallbackUkZone();
    }

    /// <summary>Current UK rules (since 1996): BST starts the last Sunday of March
    /// at 01:00 GMT and ends the last Sunday of October at 02:00 BST.</summary>
    public static TimeZoneInfo CreateFallbackUkZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(
            $"{PartnerZoneId} (built-in)",
            TimeSpan.Zero,
            "(UTC+00:00) United Kingdom",
            StandardAbbreviation,
            DaylightAbbreviation,
            [rule]);
    }
}

/// <summary>One snapshot of the partner's clock plus the viewer's own local
/// time, with every string the Home card and the overlay pill display.</summary>
public readonly record struct PartnerClockReading(
    DateTimeOffset PartnerTime,
    DateTimeOffset LocalTime,
    bool IsDaylightSaving)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public TimeSpan Offset => PartnerTime.Offset;

    /// <summary>Partner offset minus local offset: negative means the partner
    /// is behind the viewer.</summary>
    public TimeSpan DifferenceFromLocal => PartnerTime.Offset - LocalTime.Offset;

    public string ZoneAbbreviation => IsDaylightSaving
        ? PartnerClock.DaylightAbbreviation
        : PartnerClock.StandardAbbreviation;

    /// <summary>"BST · GMT+1" in summer, "GMT · GMT+0" in winter.</summary>
    public string OffsetLabel => $"{ZoneAbbreviation} · GMT{FormatOffset(Offset)}";

    /// <summary>24-hour "HH:mm".</summary>
    public string TimeText => PartnerTime.ToString("HH:mm", Invariant);

    /// <summary>":ss", shown smaller beside <see cref="TimeText"/>.</summary>
    public string SecondsText => PartnerTime.ToString("':'ss", Invariant);

    /// <summary>Lowercase weekday and date, e.g. "monday 28 sep".</summary>
    public string DateText => PartnerTime.ToString("dddd d MMM", Invariant).ToLowerInvariant();

    /// <summary>Partner calendar date minus the viewer's calendar date.</summary>
    public int DayDelta =>
        DateOnly.FromDateTime(PartnerTime.DateTime).DayNumber - DateOnly.FromDateTime(LocalTime.DateTime).DayNumber;

    public string DayText => DayDelta switch
    {
        < 0 => "still yesterday there",
        > 0 => "already tomorrow there",
        _ => "same day as you",
    };

    public string DifferenceText
    {
        get
        {
            var difference = DifferenceFromLocal;
            if (difference == TimeSpan.Zero) return "same time as you";
            var magnitude = difference.Duration();
            var hours = (int)magnitude.TotalHours;
            var amount = magnitude.Minutes == 0
                ? $"{hours} h"
                : hours == 0 ? $"{magnitude.Minutes} min" : $"{hours} h {magnitude.Minutes} min";
            return difference < TimeSpan.Zero ? $"{amount} behind you" : $"{amount} ahead of you";
        }
    }

    public bool IsNight => PartnerTime.Hour is < 7 or >= 21;

    public string DayPeriodEmoji => IsNight ? "🌙" : "☀️";

    public string MoodText => PartnerTime.Hour switch
    {
        < 6 => "probably fast asleep 💤",
        < 9 => "morning there ☕",
        < 17 => "daytime there",
        < 21 => "evening there 🌇",
        _ => "getting late there 🌙",
    };

    /// <summary>Compact overlay label, e.g. "UK 14:05".</summary>
    public string OverlayLabel => $"{PartnerClock.PartnerPlaceLabel} {TimeText}";

    /// <summary>Minute-granular screen-reader text (no seconds, so it does not
    /// change every tick).</summary>
    public string SpokenText =>
        $"time in the {PartnerClock.PartnerPlaceLabel} {TimeText}, {ZoneAbbreviation} GMT{FormatOffset(Offset)}, {DifferenceText}";

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var magnitude = offset.Duration();
        return magnitude.Minutes == 0
            ? $"{sign}{(int)magnitude.TotalHours}"
            : $"{sign}{(int)magnitude.TotalHours}:{magnitude.Minutes:00}";
    }
}
