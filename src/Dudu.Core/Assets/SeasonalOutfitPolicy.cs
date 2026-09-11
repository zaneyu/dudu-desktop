using Dudu.Core.Models;

namespace Dudu.Core.Assets;

public static class SeasonalOutfitPolicy
{
    public static string Select(
        DateOnly localDate,
        SeasonalDates dates,
        IEnumerable<string> availableKeys,
        string? manualOutfit = null)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(availableKeys);

        var available = availableKeys.ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(manualOutfit))
        {
            return available.Contains(manualOutfit)
                ? manualOutfit
                : "base";
        }

        var candidates = new List<string>(capacity: 4);
        if (dates.Anniversary is { } anniversary && anniversary.Matches(localDate))
        {
            candidates.Add("anniversary");
        }

        if (dates.Birthday is { } birthday && birthday.Matches(localDate))
        {
            candidates.Add("birthday");
        }

        if (localDate.Month == 12)
        {
            candidates.Add("winter");
        }

        candidates.Add("base");
        return candidates.FirstOrDefault(available.Contains) ?? "base";
    }
}
