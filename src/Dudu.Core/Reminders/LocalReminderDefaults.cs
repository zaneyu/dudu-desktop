using Dudu.Core.Models;

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
                "Drink some water",
                preferences.HydrationRemindersEnabled,
                new TimeOnly(10, 0),
                quietHours,
                nowUtc,
                timeZone),
            CreateReminder(
                "default-break",
                "Take a short break",
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

        var dueUtc = TimeZoneInfo.ConvertTimeToUtc(localDue, timeZone);
        return new Reminder(
            id,
            title,
            null,
            enabled,
            new RecurrenceRule.Daily(localTime),
            timeZone.Id,
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.LatestOnly,
            new DateTimeOffset(dueUtc),
            QuietHours: quietHours);
    }
}
