using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Reminders;

public static class ReminderScheduler
{
    public static DateTimeOffset? NextOccurrence(
        Reminder reminder,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!reminder.Enabled)
        {
            return null;
        }

        nowUtc = nowUtc.ToUniversalTime();

        if (reminder.SnoozedUntilUtc is { } snoozedUntilUtc
            && snoozedUntilUtc >= nowUtc)
        {
            return snoozedUntilUtc.ToUniversalTime();
        }

        var nextDueUtc = reminder.NextDueUtc.ToUniversalTime();
        if (nextDueUtc > nowUtc)
        {
            return ApplyQuietHours(reminder, nextDueUtc, timeZone);
        }

        if (reminder.Rule is RecurrenceRule.Once)
        {
            return null;
        }

        // A due occurrence that is currently quiet remains the active occurrence
        // until the quiet window ends; it must not be advanced past that boundary.
        var deferredDueUtc = ApplyQuietHours(reminder, nextDueUtc, timeZone);
        if (deferredDueUtc >= nowUtc)
        {
            return deferredDueUtc;
        }

        return reminder.Rule switch
        {
            RecurrenceRule.Daily daily => NextLocalTime(
                daily.LocalTime,
                null,
                reminder,
                nowUtc,
                timeZone),
            RecurrenceRule.SelectedWeekdays selected => NextLocalTime(
                selected.LocalTime,
                selected.Days,
                reminder,
                nowUtc,
                timeZone),
            RecurrenceRule.Interval interval => NextInterval(
                reminder,
                interval.Period,
                nowUtc,
                timeZone),
            _ => null,
        };
    }

    public static ReminderReconciliation Reconcile(
        Reminder reminder,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(timeZone);

        fromUtc = fromUtc.ToUniversalTime();
        throughUtc = throughUtc.ToUniversalTime();

        if (!reminder.Enabled || throughUtc < fromUtc)
        {
            return new ReminderReconciliation(
                Array.Empty<ReminderOccurrence>(),
                NextOccurrence(reminder, throughUtc, timeZone));
        }

        var latestIntendedUtc = LatestIntendedOccurrence(reminder, fromUtc, throughUtc, timeZone);
        var dueNow = Array.Empty<ReminderOccurrence>();
        if (latestIntendedUtc is { } intendedUtc)
        {
            var deliverableUtc = ApplyQuietHours(reminder, intendedUtc, timeZone);
            if (deliverableUtc is { } deliverable
                && deliverable >= fromUtc
                && deliverable <= throughUtc)
            {
                var occurrence = new ReminderOccurrence(reminder.Id, deliverable);
                dueNow = ReminderOccurrencePolicy
                    .Select(reminder, [occurrence])
                    .ToArray();
            }
        }

        var reminderForNext = reminder.SnoozedUntilUtc is { } snoozedUntilUtc
            && snoozedUntilUtc <= throughUtc
            ? reminder with { SnoozedUntilUtc = null }
            : reminder;
        var nextUtc = NextOccurrence(reminderForNext, throughUtc, timeZone);
        return new ReminderReconciliation(dueNow, nextUtc);
    }

    private static DateTimeOffset? LatestIntendedOccurrence(
        Reminder reminder,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo timeZone)
    {
        var nextDueUtc = reminder.NextDueUtc.ToUniversalTime();
        var snoozedUntilUtc = reminder.SnoozedUntilUtc?.ToUniversalTime();
        DateTimeOffset? latest = null;

        if (snoozedUntilUtc is { } snooze
            && snooze >= fromUtc
            && snooze <= throughUtc)
        {
            latest = snooze;
        }

        if (reminder.Rule is RecurrenceRule.Once)
        {
            if (snoozedUntilUtc is not null)
            {
                return latest;
            }

            return nextDueUtc >= fromUtc && nextDueUtc <= throughUtc
                ? nextDueUtc
                : latest;
        }

        switch (reminder.Rule)
        {
            case RecurrenceRule.Interval interval:
                if (interval.Period <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(reminder),
                        "An interval recurrence must be greater than zero.");
                }

                var intervalLatest = LatestInterval(nextDueUtc, interval.Period, throughUtc);
                if (intervalLatest is { } intervalDue
                    && intervalDue >= fromUtc
                    && !(snoozedUntilUtc is not null && intervalDue == nextDueUtc))
                {
                    latest = Max(latest, intervalDue);
                }

                break;

            case RecurrenceRule.Daily daily:
                latest = Max(latest, LatestLocalOccurrence(
                    daily.LocalTime,
                    null,
                    nextDueUtc,
                    fromUtc,
                    throughUtc,
                    timeZone,
                    snoozedUntilUtc));
                break;

            case RecurrenceRule.SelectedWeekdays selected:
                latest = Max(latest, LatestLocalOccurrence(
                    selected.LocalTime,
                    selected.Days,
                    nextDueUtc,
                    fromUtc,
                    throughUtc,
                    timeZone,
                    snoozedUntilUtc));
                break;
        }

        return latest;
    }

    private static DateTimeOffset? LatestLocalOccurrence(
        TimeOnly localTime,
        IReadOnlySet<DayOfWeek>? days,
        DateTimeOffset nextDueUtc,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo timeZone,
        DateTimeOffset? snoozedUntilUtc)
    {
        var fromLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromUtc, timeZone).DateTime)
            .AddDays(-1);
        var throughLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(throughUtc, timeZone).DateTime)
            .AddDays(1);
        DateTimeOffset? latest = null;

        for (var localDate = fromLocalDate;
             localDate <= throughLocalDate;
             localDate = localDate.AddDays(1))
        {
            if (days is not null && !days.Contains(localDate.DayOfWeek))
            {
                continue;
            }

            var candidate = ResolveLocalBoundary(
                localDate.ToDateTime(localTime, DateTimeKind.Unspecified),
                timeZone);
            if (candidate < fromUtc || candidate > throughUtc || candidate < nextDueUtc)
            {
                continue;
            }

            if (snoozedUntilUtc is not null && candidate == nextDueUtc)
            {
                continue;
            }

            latest = Max(latest, candidate);
        }

        return latest;
    }

    private static DateTimeOffset? NextLocalTime(
        TimeOnly localTime,
        IReadOnlySet<DayOfWeek>? days,
        Reminder reminder,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
        var localDate = DateOnly.FromDateTime(localNow);

        for (var dayOffset = 0; dayOffset <= 370; dayOffset++)
        {
            var candidateDate = localDate.AddDays(dayOffset);
            if (days is not null && !days.Contains(candidateDate.DayOfWeek))
            {
                continue;
            }

            var candidateUtc = ResolveLocalBoundary(
                candidateDate.ToDateTime(localTime, DateTimeKind.Unspecified),
                timeZone);
            if (candidateUtc <= nowUtc)
            {
                continue;
            }

            return ApplyQuietHours(reminder, candidateUtc, timeZone);
        }

        return null;
    }

    private static DateTimeOffset? NextInterval(
        Reminder reminder,
        TimeSpan period,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reminder), "An interval recurrence must be greater than zero.");
        }

        var nextDueUtc = reminder.NextDueUtc.ToUniversalTime();
        var elapsed = nowUtc - nextDueUtc;
        var intervals = elapsed.Ticks / period.Ticks + 1;
        var next = nextDueUtc + TimeSpan.FromTicks(period.Ticks * intervals);
        return ApplyQuietHours(reminder, next, timeZone);
    }

    private static DateTimeOffset? ApplyQuietHours(
        Reminder reminder,
        DateTimeOffset candidateUtc,
        TimeZoneInfo timeZone)
    {
        if (reminder.QuietHoursBehavior != QuietHoursBehavior.WaitUntilQuietHoursEnd
            || reminder.QuietHours is null)
        {
            return candidateUtc;
        }

        return QuietHoursPolicy.NextAllowedUtc(candidateUtc, reminder.QuietHours, timeZone);
    }

    private static DateTimeOffset? LatestInterval(
        DateTimeOffset firstDueUtc,
        TimeSpan period,
        DateTimeOffset throughUtc)
    {
        if (firstDueUtc > throughUtc)
        {
            return null;
        }

        var intervals = (throughUtc - firstDueUtc).Ticks / period.Ticks;
        return firstDueUtc + TimeSpan.FromTicks(period.Ticks * intervals);
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value >= right.Value ? left : right;
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
            return timeZone.GetAmbiguousTimeOffsets(localBoundary)
                .Select(offset => new DateTimeOffset(localBoundary, offset).ToUniversalTime())
                .OrderBy(utc => utc)
                .First();
        }

        return new DateTimeOffset(localBoundary, timeZone.GetUtcOffset(localBoundary))
            .ToUniversalTime();
    }
}
