using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.Notes;

public sealed class LocalNoteSelector
{
    private readonly ILocalNoteRepository _repository;
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly IPreferencesRepository? _preferencesRepository;
    private readonly int? _fixedDailyLimit;

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
        _fixedDailyLimit = Math.Max(0, preferences.LocalNoteDailyLimit);
    }

    public LocalNoteSelector(
        ILocalNoteRepository repository,
        IClock clock,
        IRandomSource random,
        IPreferencesRepository preferencesRepository,
        Preferences fallbackPreferences)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _preferencesRepository = preferencesRepository ?? throw new ArgumentNullException(nameof(preferencesRepository));
        ArgumentNullException.ThrowIfNull(fallbackPreferences);
        _fixedDailyLimit = Math.Max(0, fallbackPreferences.LocalNoteDailyLimit);
    }

    public async Task<LocalLoveNote?> SelectAsync(
        bool manualRequest,
        CancellationToken cancellationToken = default)
    {
        var shownUtc = _clock.UtcNow.ToUniversalTime();
        var localDate = LocalDate(shownUtc);
        var dailyLimit = await ReadDailyLimitAsync(cancellationToken);
        if (!manualRequest)
        {
            var unsolicitedCount = await _repository.CountUnsolicitedShownAsync(
                localDate,
                cancellationToken);
            if (unsolicitedCount >= dailyLimit)
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

        if (eligible.Length == 0 && manualRequest)
        {
            // Skipping the two most recent notes keeps unprompted picks fresh,
            // but with only one or two enabled notes it leaves nothing -- and a
            // pick she explicitly asked for must not fail as if the jar were
            // empty. A manual request falls back to every enabled note.
            eligible = notes.ToArray();
        }

        if (eligible.Length == 0)
        {
            return null;
        }

        var selected = eligible[NextIndex(eligible.Length)];
        var recorded = await _repository.TryRecordShownAsync(
            selected.Id,
            shownUtc,
            localDate,
            dailyLimit,
            unsolicited: !manualRequest,
            cancellationToken);
        return recorded ? selected : null;
    }

    private async Task<int> ReadDailyLimitAsync(CancellationToken cancellationToken)
    {
        var preferences = _preferencesRepository is null
            ? null
            : await _preferencesRepository.GetAsync(cancellationToken);
        return Math.Max(0, preferences?.LocalNoteDailyLimit ?? _fixedDailyLimit ?? 0);
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
