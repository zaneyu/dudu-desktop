namespace Dudu.Core.Models;

public sealed record TaskItem(
    Guid Id,
    string Title,
    string? Notes,
    DateTimeOffset? DueUtc,
    bool IsCompleted,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc)
{
    public TaskItem(
        Guid id,
        string title,
        string? notes,
        bool isCompleted,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        DateTimeOffset? completedUtc)
        : this(id, title, notes, null, isCompleted, createdUtc, updatedUtc, completedUtc)
    {
    }
}
