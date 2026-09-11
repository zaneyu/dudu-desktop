using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.Notes;

public sealed class LocalNoteSelector
{
    private readonly ILocalNoteRepository _repository;
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly int _dailyLimit;

    public LocalNoteSelector(
        ILocalNoteRepository repository,
        IClock clock,
        IRandomSource random,
        Preferences preferences)
        : this(clock, random, preferences, repository)
    {
    }

    public LocalNoteSelector(
        IClock clock,
        IRandomSource random,
        Preferences preferences,
        ILocalNoteRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        ArgumentNullException.ThrowIfNull(preferences);
        _dailyLimit = Math.Max(0, preferences.LocalNoteDailyLimit);
    }

    public async Task<LocalLoveNote?> SelectAsync(
        bool manualRequest,
        CancellationToken cancellationToken = default)
    {
        var localDate = LocalDate(_clock.UtcNow);
        if (!manualRequest)
        {
            var unsolicitedCount = await _repository.CountUnsolicitedShownAsync(
                localDate,
                cancellationToken);
            if (unsolicitedCount >= _dailyLimit)
            {
                return null;
            }
        }

        var notes = await _repository.ListEnabledAsync(cancellationToken);
        if (notes.Count == 0)
        {
            return null;
        }

        var recentIds = await _repository.GetMostRecentShownIdsAsync(
            2,
            cancellationToken);
        var recent = recentIds.ToHashSet(StringComparer.Ordinal);
        var eligible = notes
            .Where(note => !recent.Contains(note.Id))
            .ToArray();

        if (eligible.Length == 0)
        {
            return null;
        }

        var selected = eligible[NextIndex(eligible.Length)];
        await _repository.RecordShownAsync(
            selected.Id,
            _clock.UtcNow.ToUniversalTime(),
            unsolicited: !manualRequest,
            cancellationToken);
        return selected;
    }

    private int NextIndex(int exclusiveMax)
    {
        var index = _random.Next(exclusiveMax);
        if (index < 0 || index >= exclusiveMax)
        {
            throw new InvalidOperationException(
                $"Random source returned {index}; expected a value from 0 through {exclusiveMax - 1}.");
        }

        return index;
    }

    private DateOnly LocalDate(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            utcNow.ToUniversalTime(),
            _clock.LocalTimeZone).DateTime);
}
