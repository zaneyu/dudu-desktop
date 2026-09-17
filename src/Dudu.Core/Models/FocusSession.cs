namespace Dudu.Core.Models;

public enum FocusStatus
{
    Running,
    Paused,
    Completed,
    EndedEarly,
}

public sealed record FocusSession(
    Guid Id,
    Guid? TaskId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndsUtc,
    TimeSpan RemainingWhenPaused,
    FocusStatus Status,
    DateTimeOffset UpdatedUtc,
    TimeSpan ConsumedFocusTime = default);

public sealed record FocusSnapshot(
    Guid Id,
    Guid? TaskId,
    FocusStatus Status,
    TimeSpan Remaining);
