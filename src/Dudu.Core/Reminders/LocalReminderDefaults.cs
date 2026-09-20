using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Reminders;

public static class LocalReminderDefaults
{
    public const string EveningCheckInId = "default-evening-checkin";
    public const string BedtimeId = "default-bedtime";

    /// <summary>Neutral copy shipped for the evening check-in title. No token is ever
    /// persisted; personalisation happens at display time via <see cref="PersonalizeTitle"/>.</summary>
    public const string EveningCheckInDefaultTitle = "how was your day?";

    /// <summary>The evening check-in title as it shipped before display-time
    /// personalisation existed. Rows created by that version have this text baked
    /// in verbatim (with "ada" hardcoded) and are recognised here so untouched
    /// installs still personalise instead of always saying "ada".</summary>
    public const string EveningCheckInLegacyTitle = "how was your day, ada?";

    public const string BedtimeDefaultTitle = "shuijiaojiao";

    public const string BedtimeLegacyTitle = "shuijiaojiao, ada";

    public const string BedtimeDefaultDetails = "time to wind down. goodnight.";

    public const string BedtimeLegacyDetails = "time to wind down. goodnight, ada.";

    /// <summary>
    /// Personalises a reminder's stored title or details text for display (toast,
    /// Home summary) without ever rewriting what is persisted. Returns the
    /// personalised copy only when <paramref name="reminderId"/> is one of the
    /// default reminders and <paramref name="storedText"/> exactly matches that
    /// reminder's neutral text or its legacy "ada" text; otherwise the stored text
    /// -- including anything the user has edited -- passes through unchanged.
    /// </summary>
    public static string PersonalizeTitle(string reminderId, string storedText, string? recipientName)
    {
        ArgumentNullException.ThrowIfNull(reminderId);
        ArgumentNullException.ThrowIfNull(storedText);

        var trimmedName = recipientName?.Trim();
        var clause = string.IsNullOrEmpty(trimmedName) ? string.Empty : $", {trimmedName}";

        if (reminderId == EveningCheckInId
            && (storedText == EveningCheckInDefaultTitle || storedText == EveningCheckInLegacyTitle))
        {
            return $"how was your day{clause}?";
        }

        if (reminderId == BedtimeId
            && (storedText == BedtimeDefaultTitle || storedText == BedtimeLegacyTitle))
        {
            return $"shuijiaojiao{clause}";
        }

        if (reminderId == BedtimeId
            && (storedText == BedtimeDefaultDetails || storedText == BedtimeLegacyDetails))
        {
            return $"time to wind down. goodnight{clause}.";
        }

        return storedText;
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
                EveningCheckInDefaultTitle,
                preferences.EveningCheckInEnabled,
                new TimeOnly(20, 0),
                null,
                nowUtc,
                timeZone,
                "a little space to reflect. your check-in stays on this device.",
                MissedOccurrencePolicy.Skip),
            CreateReminder(
                BedtimeId,
                BedtimeDefaultTitle,
                preferences.BedtimeRitualEnabled,
                new TimeOnly(22, 0),
                null,
                nowUtc,
                timeZone,
                BedtimeDefaultDetails,
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
