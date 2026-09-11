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
    DateTimeOffset NextDueUtc,
    DateTimeOffset? SnoozedUntilUtc = null,
    QuietHours? QuietHours = null);

public sealed record ReminderOccurrence(
    string ReminderId,
    DateTimeOffset DueUtc);
