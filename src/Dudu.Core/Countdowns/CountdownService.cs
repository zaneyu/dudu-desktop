using Dudu.Core.Models;

namespace Dudu.Core.Countdowns;

public static class CountdownService
{
    public static CountdownDisplay GetDisplay(
        Countdown countdown,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(countdown);
        nowUtc = nowUtc.ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, countdown.LocalTimeZone);

        if (countdown.IsAllDay || countdown.TargetDate is not null)
        {
            var targetDate = countdown.TargetDate
                ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                    countdown.TargetUtc!.Value,
                    countdown.LocalTimeZone).DateTime);
            var today = DateOnly.FromDateTime(localNow.DateTime);
            var calendarDays = Math.Max(0, targetDate.DayNumber - today.DayNumber);
            return new CountdownDisplay(TimeSpan.FromDays(calendarDays), calendarDays);
        }

        if (countdown.TargetUtc is not { } targetUtc)
        {
            return new CountdownDisplay(TimeSpan.Zero, 0);
        }

        var remaining = targetUtc.ToUniversalTime() - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return new CountdownDisplay(TimeSpan.Zero, 0);
        }

        return new CountdownDisplay(
            remaining,
            (int)Math.Floor(remaining.TotalDays));
    }
}
