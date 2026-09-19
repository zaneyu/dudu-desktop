using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Reminders;

public static class LocalReminderDefaults
{
    public const string EveningCheckInId = "default-evening-checkin";
    public const string BedtimeId = "default-bedtime";

    /// <summary>
    /// Marks where a recipient's name belongs in default reminder text.
    /// Reminders created before this token existed were persisted with a
    /// name already baked in (e.g. "ada") and contain no token, so
    /// <see cref="ApplyRecipientName"/> leaves them untouched -- no
    /// migration of stored rows is needed.
    /// </summary>
    public const string RecipientNameToken = "{recipient}";

    /// <summary>
    /// Expands <see cref="RecipientNameToken"/> with ", name" when a name is
    /// given, or drops it entirely when the name is empty or whitespace so
    /// the copy still reads naturally.
    /// </summary>
    public static string ApplyRecipientName(string text, string? recipientName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = recipientName?.Trim();
        var suffix = string.IsNullOrEmpty(trimmed) ? string.Empty : $", {trimmed}";
        return text.Replace(RecipientNameToken, suffix);
    }

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
            CreateReminder(
                EveningCheckInId,
                $"how was your day{RecipientNameToken}?",
                preferences.EveningCheckInEnabled,
                new TimeOnly(20, 0),
                null,
                nowUtc,
                timeZone,
                "a little space to reflect. your check-in stays on this device.",
                MissedOccurrencePolicy.Skip),
            CreateReminder(
                BedtimeId,
                $"shuijiaojiao{RecipientNameToken}",
                preferences.BedtimeRitualEnabled,
                new TimeOnly(22, 0),
                null,
                nowUtc,
                timeZone,
                $"time to wind down. goodnight{RecipientNameToken}.",
                MissedOccurrencePolicy.Skip),
        ];
    }

    private static Reminder CreateReminder(
        string id,
        string title,
        bool enabled,
        TimeOnly localTime,
        QuietHours? quietHours,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        string? details = null,
        MissedOccurrencePolicy missedPolicy = MissedOccurrencePolicy.LatestOnly)
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

        var dueUtc = quietHours is null
            ? resolvedUtc
            : QuietHoursPolicy.NextAllowedUtc(resolvedUtc, quietHours, timeZone);
        return new Reminder(
            id,
            title,
            details,
            enabled,
            new RecurrenceRule.Daily(localTime),
            timeZone.Id,
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            missedPolicy,
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
