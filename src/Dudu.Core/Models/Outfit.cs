namespace Dudu.Core.Models;

public sealed record Outfit(string Key, string? DisplayName = null);

public readonly record struct MonthDay(int Month, int Day)
{
    /// <summary>Feb 29 matches Feb 28 in non-leap years so leap-day
    /// anniversaries and birthdays still celebrate every year.</summary>
    public bool Matches(DateOnly date) =>
        (date.Month == Month && date.Day == Day)
        || (Month == 2 && Day == 29 && date.Month == 2 && date.Day == 28
            && !DateTime.IsLeapYear(date.Year));
}

public sealed record SeasonalDates(
    MonthDay? Anniversary,
    MonthDay? Birthday)
{
    public static SeasonalDates Empty { get; } = new(null, null);
}
