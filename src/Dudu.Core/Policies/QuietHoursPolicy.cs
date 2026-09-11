using Dudu.Core.Models;

namespace Dudu.Core.Policies;

public static class QuietHoursPolicy
{
    public static bool IsQuiet(
        DateTimeOffset utc,
        QuietHours quietHours,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!quietHours.Enabled)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(utc, timeZone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        if (quietHours.Start == quietHours.End)
        {
            return true;
        }

        return quietHours.Start < quietHours.End
            ? localTime >= quietHours.Start && localTime < quietHours.End
            : localTime >= quietHours.Start || localTime < quietHours.End;
    }

    public static DateTimeOffset NextAllowedUtc(
        DateTimeOffset utc,
        QuietHours quietHours,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!quietHours.Enabled)
        {
            return utc;
        }

        var local = TimeZoneInfo.ConvertTime(utc, timeZone);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        if (quietHours.Start != quietHours.End && !IsQuiet(local, quietHours))
        {
            return utc;
        }

        if (quietHours.Start == quietHours.End)
        {
            localDate = localDate.AddDays(1);
        }
        else if (quietHours.Start > quietHours.End && localTime >= quietHours.Start)
        {
            localDate = localDate.AddDays(1);
        }

        var localEnd = localDate.ToDateTime(quietHours.End, DateTimeKind.Unspecified);
        return ResolveLocalBoundary(localEnd, timeZone);
    }

    private static bool IsQuiet(
        DateTimeOffset local,
        QuietHours quietHours)
    {
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        return quietHours.Start < quietHours.End
            ? localTime >= quietHours.Start && localTime < quietHours.End
            : localTime >= quietHours.Start || localTime < quietHours.End;
    }

    private static DateTimeOffset ResolveLocalBoundary(
        DateTime localBoundary,
        TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(localBoundary))
        {
            localBoundary = localBoundary.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(localBoundary))
        {
            var earliestUtc = timeZone.GetAmbiguousTimeOffsets(localBoundary)
                .Select(offset => new DateTimeOffset(localBoundary, offset).ToUniversalTime())
                .Min();

            return earliestUtc;
        }

        var offsetAtBoundary = timeZone.GetUtcOffset(localBoundary);
        return new DateTimeOffset(localBoundary, offsetAtBoundary).ToUniversalTime();
    }
}
