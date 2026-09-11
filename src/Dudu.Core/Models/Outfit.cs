namespace Dudu.Core.Models;

public sealed record Outfit(string Key, string? DisplayName = null);

public readonly record struct MonthDay(int Month, int Day)
{
    public bool Matches(DateOnly date) => date.Month == Month && date.Day == Day;
}

public sealed record SeasonalDates(
    MonthDay? Anniversary,
    MonthDay? Birthday)
{
    public static SeasonalDates Empty { get; } = new(null, null);
}
