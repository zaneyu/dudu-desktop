using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.CheckIns;

public sealed class CheckInService
{
    private const int MaxNoteScalars = 2_000;

    private readonly ICheckInRepository _repository;
    private readonly IClock _clock;

    public CheckInService(ICheckInRepository repository, IClock clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<MoodCheckIn> RecordAsync(
        MoodChoice choice,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ValidateChoice(choice);
        var checkIn = new MoodCheckIn(
            Guid.NewGuid(),
            choice,
            NormalizeNote(note),
            _clock.UtcNow.ToUniversalTime());

        await _repository.SaveAsync(checkIn, cancellationToken);
        return checkIn;
    }

    public Task<MoodCheckIn> RecordAsync(
        MoodChoice choice,
        CancellationToken cancellationToken = default) =>
        RecordAsync(choice, null, cancellationToken);

    public async Task<CheckInSummary> SummarizeAsync(
        int localDays,
        CancellationToken cancellationToken = default)
    {
        if (localDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localDays));
        }

        var today = LocalDate(_clock.UtcNow);
        var firstDate = today.AddDays(-(localDays - 1));
        var sinceUtc = StartOfLocalDateUtc(firstDate);
        var checkIns = await _repository.ListSinceAsync(sinceUtc, cancellationToken);

        var inWindow = checkIns
            .Where(checkIn =>
            {
                var date = LocalDate(checkIn.CreatedUtc);
                return date >= firstDate && date <= today;
            })
            .OrderByDescending(checkIn => checkIn.CreatedUtc)
            .ToArray();

        var counts = Enum.GetValues<MoodChoice>()
            .ToDictionary(choice => choice, _ => 0);
        foreach (var checkIn in inWindow)
        {
            counts[checkIn.Choice]++;
        }

        return new CheckInSummary(counts, inWindow);
    }

    private DateOnly LocalDate(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            utcNow.ToUniversalTime(),
            _clock.LocalTimeZone).DateTime);

    private DateTimeOffset StartOfLocalDateUtc(DateOnly localDate)
    {
        var local = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (_clock.LocalTimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(local, _clock.LocalTimeZone));
    }

    private static string? NormalizeNote(string? note)
    {
        var normalized = note?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.EnumerateRunes().Count() > MaxNoteScalars)
        {
            throw new ArgumentException(
                $"Check-in note cannot exceed {MaxNoteScalars} Unicode scalar values.",
                nameof(note));
        }

        return normalized;
    }

    private static void ValidateChoice(MoodChoice choice)
    {
        if (!Enum.IsDefined(choice))
        {
            throw new ArgumentOutOfRangeException(nameof(choice));
        }
    }
}
