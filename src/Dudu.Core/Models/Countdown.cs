namespace Dudu.Core.Models;

public sealed record Countdown
{
    public Countdown(
        string id,
        string title,
        DateTimeOffset? targetUtc,
        DateOnly? targetDate,
        bool isAllDay = false,
        TimeZoneInfo? localTimeZone = null)
    {
        Id = id;
        Title = title;
        TargetUtc = targetUtc?.ToUniversalTime();
        TargetDate = targetDate;
        IsAllDay = isAllDay || targetDate is not null;
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public Countdown(string id, string title)
        : this(id, title, (DateTimeOffset?)null, (DateOnly?)null, false)
    {
    }

    public Countdown(
        string id,
        string title,
        DateTimeOffset? targetUtc,
        bool isAllDay,
        TimeZoneInfo? localTimeZone = null)
        : this(id, title, targetUtc, null, isAllDay, localTimeZone)
    {
    }

    public Countdown(
        string id,
        string title,
        DateOnly? targetDate,
        bool isAllDay,
        TimeZoneInfo? localTimeZone = null)
        : this(id, title, null, targetDate, isAllDay, localTimeZone)
    {
    }

    public Countdown(string id, string title, DateOnly targetDate)
        : this(id, title, null, targetDate, true)
    {
    }

    public Countdown(string id, string title, DateOnly targetDate, TimeZoneInfo localTimeZone)
        : this(id, title, null, targetDate, true, localTimeZone)
    {
    }

    public Countdown(string id, string title, DateTimeOffset targetUtc)
        : this(id, title, targetUtc, null)
    {
    }

    public Countdown(
        string id,
        string title,
        DateOnly? targetDate,
        DateTimeOffset? targetUtc,
        bool isAllDay = false,
        TimeZoneInfo? localTimeZone = null)
        : this(id, title, targetUtc, targetDate, isAllDay, localTimeZone)
    {
    }

    public string Id { get; }

    public string Title { get; }

    public DateTimeOffset? TargetUtc { get; }

    public DateOnly? TargetDate { get; }

    public bool IsAllDay { get; }

    public TimeZoneInfo LocalTimeZone { get; }
}

public sealed record CountdownDisplay(TimeSpan Remaining, int CalendarDays)
{
    public int Days => CalendarDays;
}
