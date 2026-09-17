using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Reminders;

public static class ReminderScheduler
{
    public static readonly TimeSpan OnTimeGracePeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Creation/update boundary guard. Call this before persisting a reminder so
    /// corrupt schedules (an empty weekday set that would never fire, a
    /// <see cref="DateTimeOffset.MinValue"/> sentinel that would sort before every
    /// real due time, or a missing due time on an enabled recurring reminder) are
    /// rejected loudly instead of rotting in the store.
    /// </summary>
    public static void ValidateForSave(Reminder reminder)
    {
        ArgumentNullException.ThrowIfNull(reminder);

        if (reminder.Rule is RecurrenceRule.SelectedWeekdays selected
            && (selected.Days is null || selected.Days.Count == 0
                || selected.Days.Any(day => !Enum.IsDefined(day))))
        {
            throw new ArgumentException(
                "A selected-weekdays reminder must include only valid days and at least one day.",
                nameof(reminder));
        }

        if (reminder.Rule is RecurrenceRule.Interval interval && interval.Period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminder),
                "An interval recurrence must be greater than zero.");
        }

        if (reminder.NextDueUtc == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminder),
                "NextDueUtc must not be DateTimeOffset.MinValue; use null for a completed reminder.");
        }

        if (reminder.NextDueUtc is null
            && reminder.Enabled
            && reminder.Rule is not RecurrenceRule.Once)
        {
            throw new ArgumentException(
                "An enabled recurring reminder must have a NextDueUtc.",
                nameof(reminder));
        }
    }

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

        if (reminder.NextDueUtc is null)
        {
            return null;
        }

        if (reminder.SnoozedUntilUtc is { } snoozedUntilUtc
            && snoozedUntilUtc >= nowUtc)
        {
            return snoozedUntilUtc.ToUniversalTime();
        }

        var nextDueUtc = reminder.NextDueUtc.Value.ToUniversalTime();
        if (nextDueUtc > nowUtc)
        {
            return ApplyQuietHours(reminder, nextDueUtc, timeZone);
        }

        // A due occurrence that is currently quiet remains the active occurrence
        // until the quiet window ends; it must not be advanced past that boundary.
        var deferredDueUtc = ApplyQuietHours(reminder, nextDueUtc, timeZone);
        if (deferredDueUtc > nowUtc)
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
        TimeZoneInfo timeZone,
        Action<string>? onCollapsed = null)
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

        var (latestIntendedUtc, windowCount) = LatestIntendedOccurrence(reminder, fromUtc, throughUtc, timeZone);
        var dueNow = Array.Empty<ReminderOccurrence>();
        if (latestIntendedUtc is { } intendedUtc)
        {
            var deliverableUtc = ApplyQuietHours(reminder, intendedUtc, timeZone);
            if (deliverableUtc > throughUtc)
            {
                return new ReminderReconciliation(dueNow, deliverableUtc);
            }

            if (deliverableUtc is { } deliverable
                && deliverable >= fromUtc
                && deliverable <= throughUtc)
            {
                var occurrence = new ReminderOccurrence(reminder.Id, deliverable);
                dueNow = ReminderOccurrencePolicy
                    .Select(reminder, [occurrence], throughUtc - deliverable > OnTimeGracePeriod)
                    .ToArray();
                if (dueNow.Length > 0 && ApplyQuietHours(reminder, throughUtc, timeZone) is { } allowedUtc
                    && allowedUtc > throughUtc)
                {
                    return new ReminderReconciliation(Array.Empty<ReminderOccurrence>(), allowedUtc);
                }

                // A sleep-length window can span many missed occurrences that the
                // LatestOnly policy folds into a single delivery. Surface that so
                // operators can see collapse happening instead of wondering where
                // the missed occurrences went.
                if (dueNow.Length > 0 && windowCount > dueNow.Length)
                {
                    onCollapsed?.Invoke(
                        $"Reminder '{reminder.Id}' collapsed {windowCount} occurrences " +
                        $"between {fromUtc:O} and {throughUtc:O} to the latest only.");
                }
            }
        }

        var reminderForNext = reminder.SnoozedUntilUtc is { } snoozedUntilUtc
            && snoozedUntilUtc <= throughUtc
            ? reminder with { SnoozedUntilUtc = null }
            : reminder;
        var nextUtc = NextOccurrence(reminderForNext, throughUtc, timeZone);
        return new ReminderReconciliation(dueNow, nextUtc);
    }

    private static (DateTimeOffset? Latest, int WindowCount) LatestIntendedOccurrence(
        Reminder reminder,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo timeZone)
    {
        if (reminder.NextDueUtc is null)
        {
            return (null, 0);
        }

        var nextDueUtc = reminder.NextDueUtc.Value.ToUniversalTime();
        var snoozedUntilUtc = reminder.SnoozedUntilUtc?.ToUniversalTime();
        DateTimeOffset? latest = null;
        var windowCount = 0;

        // A base occurrence already counted here must not be counted again by the
        // rule arms below; the count tracks distinct intended instants only.
        var baseCounted = false;

        // NextDueUtc may itself be a persisted quiet-hour deferral rather than
        // a recurrence boundary. It is still the pending occurrence to deliver.
        if (snoozedUntilUtc is null
            && nextDueUtc >= fromUtc
            && nextDueUtc <= throughUtc)
        {
            latest = nextDueUtc;
            windowCount++;
            baseCounted = true;
        }

        if (snoozedUntilUtc is { } snooze
            && snooze >= fromUtc
            && snooze <= throughUtc)
        {
            latest = snooze;
            windowCount++;
        }

        if (reminder.Rule is RecurrenceRule.Once)
        {
            if (snoozedUntilUtc is not null)
            {
                return (latest, windowCount);
            }

            return nextDueUtc >= fromUtc && nextDueUtc <= throughUtc
                ? (nextDueUtc, windowCount)
                : (latest, windowCount);
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

                windowCount += CountInterval(
                    nextDueUtc,
                    interval.Period,
                    fromUtc,
                    throughUtc,
                    excludeAnchor: snoozedUntilUtc is not null || baseCounted);
                break;

            case RecurrenceRule.Daily daily:
                latest = Max(latest, LatestLocalOccurrence(
                    daily.LocalTime,
                    null,
                    nextDueUtc,
                    fromUtc,
                    throughUtc,
                    timeZone,
                    snoozedUntilUtc,
                    baseCounted ? nextDueUtc : null,
                    snoozedUntilUtc,
                    out var dailyCount));
                windowCount += dailyCount;
                break;

            case RecurrenceRule.SelectedWeekdays selected:
                latest = Max(latest, LatestLocalOccurrence(
                    selected.LocalTime,
                    selected.Days,
                    nextDueUtc,
                    fromUtc,
                    throughUtc,
                    timeZone,
                    snoozedUntilUtc,
                    baseCounted ? nextDueUtc : null,
                    snoozedUntilUtc,
                    out var weekdayCount));
                windowCount += weekdayCount;
                break;
        }

        return (latest, windowCount);
    }

    private static DateTimeOffset? LatestLocalOccurrence(
        TimeOnly localTime,
        IReadOnlySet<DayOfWeek>? days,
        DateTimeOffset nextDueUtc,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo timeZone,
        DateTimeOffset? snoozedUntilUtc,
        DateTimeOffset? alreadyCounted,
        DateTimeOffset? snoozeCounted,
        out int windowCount)
    {
        var fromLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromUtc, timeZone).DateTime)
            .AddDays(-1);
        var throughLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(throughUtc, timeZone).DateTime)
            .AddDays(1);
        DateTimeOffset? latest = null;
        windowCount = 0;

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

            if (candidate == alreadyCounted || candidate == snoozeCounted)
            {
                continue;
            }

            windowCount++;
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

        if (reminder.NextDueUtc is null)
        {
            return null;
        }

        var nextDueUtc = reminder.NextDueUtc.Value.ToUniversalTime();
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

    private static int CountInterval(
        DateTimeOffset firstDueUtc,
        TimeSpan period,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        bool excludeAnchor)
    {
        if (firstDueUtc > throughUtc)
        {
            return 0;
        }

        var lastOffset = (throughUtc - firstDueUtc).Ticks / period.Ticks;
        long firstOffset = 0;
        if (firstDueUtc < fromUtc)
        {
            firstOffset = ((fromUtc - firstDueUtc).Ticks + period.Ticks - 1) / period.Ticks;
        }

        var count = lastOffset - firstOffset + 1;
        if (count <= 0)
        {
            return 0;
        }

        // The anchor instant (k = 0) was already counted by the base/snooze arm.
        if (excludeAnchor && firstOffset == 0)
        {
            count--;
        }

        return count > int.MaxValue ? int.MaxValue : (int)count;
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
