namespace Dudu.Core.Models;

public enum MoodChoice
{
    Great,
    Okay,
    Tired,
    Rough,
}

public sealed record MoodCheckIn(
    Guid Id,
    MoodChoice Choice,
    string? Note,
    DateTimeOffset CreatedUtc)
{
    public MoodCheckIn(
        MoodChoice choice,
        string? note,
        DateTimeOffset createdUtc)
        : this(Guid.NewGuid(), choice, note, createdUtc)
    {
    }

    public MoodChoice Mood => Choice;
}

public sealed record CheckInSummary(
    IReadOnlyDictionary<MoodChoice, int> Counts,
    IReadOnlyList<MoodCheckIn> Recent);
