using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Reminders;

public static class LocalReminderDefaults
{
    public static IReadOnlyList<Reminder> Create(
        Preferences preferences,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(timeZone);

        var quietHours = preferences.QuietHours;
        return
        [
            CreateReminder(
                "default-hydration",
                "drink water lor",
                preferences.HydrationRemindersEnabled,
                new TimeOnly(10, 0),
                quietHours,
                nowUtc,
                timeZone),
            CreateReminder(
                "default-break",
                "go take a short break",
                preferences.BreakRemindersEnabled,
                new TimeOnly(14, 0),
                quietHours,
                nowUtc,
                timeZone),
        ];
    }

    private static Reminder CreateReminder(
        string id,
        string title,
        bool enabled,
        TimeOnly localTime,
        QuietHours quietHours,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var localDue = date.ToDateTime(localTime, DateTimeKind.Unspecified);
        if (localDue <= localNow.DateTime)
        {
            localDue = localDue.AddDays(1);
        }

        // Resolve the wall-clock due the same way the scheduler does: forward-shift
        // out of DST gaps instead of throwing, and take the earlier instant of an
        // ambiguous fall-back hour so the default never lands an hour late.
        var resolvedUtc = ResolveLocalDue(localDue, timeZone);

        // The initial due must honor quiet hours like every later occurrence: a
        // default that lands inside the quiet window defers to its end instead of
        // firing (or going stale) the moment it is seeded.
        var dueUtc = QuietHoursPolicy.NextAllowedUtc(resolvedUtc, quietHours, timeZone);
        return new Reminder(
            id,
            title,
            null,
            enabled,
            new RecurrenceRule.Daily(localTime),
            timeZone.Id,
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.LatestOnly,
            dueUtc,
            QuietHours: quietHours);
    }

    private static DateTimeOffset ResolveLocalDue(DateTime localDue, TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(localDue))
        {
            localDue = localDue.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(localDue))
        {
            return timeZone.GetAmbiguousTimeOffsets(localDue)
                .Select(offset => new DateTimeOffset(localDue, offset).ToUniversalTime())
                .OrderBy(utc => utc)
                .First();
        }

        return new DateTimeOffset(localDue, timeZone.GetUtcOffset(localDue)).ToUniversalTime();
    }
}
