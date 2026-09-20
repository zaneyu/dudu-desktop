namespace Dudu.Core.Models;

public enum QuietHoursBehavior
{
    DeliverImmediately,
    WaitUntilQuietHoursEnd,
}

public enum MissedOccurrencePolicy
{
    LatestOnly,
    Skip,
}

public abstract record RecurrenceRule
{
    public sealed record Once : RecurrenceRule;

    public sealed record Daily(TimeOnly LocalTime) : RecurrenceRule;

    public sealed record SelectedWeekdays(
        IReadOnlySet<DayOfWeek> Days,
        TimeOnly LocalTime) : RecurrenceRule
    {
        public SelectedWeekdays(IEnumerable<DayOfWeek> days, TimeOnly localTime)
            : this(new HashSet<DayOfWeek>(days), localTime)
        {
        }

        // The record-synthesized equality would compare Days by reference (sets
        // do not override Equals/GetHashCode), so two rules with the identical
        // selected days as different set instances would never compare equal.
        // Compare by set membership instead. Days is non-nullable by
        // declaration, but a `with` expression or deserialization can still
        // hand back a null (ReminderScheduler defends against exactly this),
        // so treat null as an empty set rather than throwing.
        public bool Equals(SelectedWeekdays? other)
        {
            if (other is null || LocalTime != other.LocalTime)
            {
                return false;
            }

            if (Days is null || other.Days is null)
            {
                return Days is null && other.Days is null;
            }

            return Days.SetEquals(other.Days);
        }

        public override int GetHashCode()
        {
            var daysHash = 0;
            if (Days is not null)
            {
                foreach (var day in Days)
                {
                    daysHash ^= day.GetHashCode();
                }
            }

            return HashCode.Combine(LocalTime, daysHash);
        }
    }

    public sealed record Interval(
        TimeSpan Period,
        DateTimeOffset? FirstDueUtc = null) : RecurrenceRule;
}

public sealed record Reminder(
    string Id,
    string Title,
    string? Details,
    bool Enabled,
    RecurrenceRule Rule,
    string LocalTimeZoneId,
    QuietHoursBehavior QuietHoursBehavior,
    MissedOccurrencePolicy MissedPolicy,
    DateTimeOffset? NextDueUtc,
    DateTimeOffset? SnoozedUntilUtc = null,
    QuietHours? QuietHours = null);

public sealed record ReminderOccurrence(
    string ReminderId,
    DateTimeOffset DueUtc);
