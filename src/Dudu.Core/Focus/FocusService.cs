using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.Focus;

public sealed class FocusService
{
    private readonly IFocusSessionRepository _repository;
    private readonly ITaskRepository? _taskRepository;
    private readonly IClock _clock;

    public FocusService(IFocusSessionRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public FocusService(
        IFocusSessionRepository repository,
        ITaskRepository taskRepository,
        IClock clock)
    {
        _repository = repository;
        _taskRepository = taskRepository;
        _clock = clock;
    }

    public FocusService(
        IFocusSessionRepository repository,
        IClock clock,
        ITaskRepository taskRepository)
        : this(repository, taskRepository, clock)
    {
    }

    public async Task<FocusSnapshot> StartAsync(
        Guid? taskId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "aiyo focus duration must be positive");
        }

        if (taskId is not null)
        {
            if (_taskRepository is null)
            {
                throw new InvalidOperationException(
                    "oh no task focus isnt wired up");
            }

            var task = await _taskRepository.GetAsync(taskId.Value, cancellationToken);
            if (task is null || task.IsCompleted)
            {
                throw new InvalidOperationException(
                    "cannot start focus task is missing or done");
            }
        }

        var now = UtcNow();
        var session = new FocusSession(
            Guid.NewGuid(),
            taskId,
            now,
            now.Add(duration),
            TimeSpan.Zero,
            FocusStatus.Running,
            now);

        if (!await _repository.TryCreateActiveAsync(session, cancellationToken))
        {
            throw new InvalidOperationException(
                "alala another focus session is active");
        }

        return ToSnapshot(session, now);
    }

    public async Task<FocusSnapshot?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var session = await _repository.GetActiveAsync(cancellationToken);
        return session is null ? null : ToSnapshot(session, UtcNow());
    }

    public async Task<FocusSnapshot> PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetRequiredAsync(id, cancellationToken);
        EnsureStatus(session, FocusStatus.Running, "pause");
        var now = UtcNow();
        var remaining = RemainingForRunning(session, now);
        if (remaining <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("wait cant pause an expired focus session");
        }

        var paused = session with
        {
            EndsUtc = null,
            RemainingWhenPaused = remaining,
            Status = FocusStatus.Paused,
            UpdatedUtc = now,
        };
        if (!await _repository.TryCompareAndSetAsync(session, paused, cancellationToken))
        {
            throw TransitionConflict("pause");
        }

        return ToSnapshot(paused, now);
    }

    public async Task<FocusSnapshot> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetRequiredAsync(id, cancellationToken);
        EnsureStatus(session, FocusStatus.Paused, "resume");
        var now = UtcNow();
        if (session.RemainingWhenPaused <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("aiyo no time left to resume focus");
        }

        var resumed = session with
        {
            EndsUtc = now.Add(session.RemainingWhenPaused),
            RemainingWhenPaused = TimeSpan.Zero,
            Status = FocusStatus.Running,
            UpdatedUtc = now,
        };
        if (!await _repository.TryCompareAndSetAsync(session, resumed, cancellationToken))
        {
            throw TransitionConflict("resume");
        }

        return ToSnapshot(resumed, now);
    }

    public async Task<FocusSnapshot> ExtendAsync(
        Guid id,
        TimeSpan extension,
        CancellationToken cancellationToken)
    {
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "oh no focus extension must be positive");
        }

        var session = await GetRequiredAsync(id, cancellationToken);
        var now = UtcNow();
        FocusSession extended;
        if (session.Status == FocusStatus.Running)
        {
            if (session.EndsUtc is null || session.EndsUtc <= now)
            {
                throw new InvalidOperationException("cannot extend an expired focus session");
            }

            extended = session with
            {
                EndsUtc = session.EndsUtc.Value.Add(extension),
                UpdatedUtc = now,
            };
        }
        else if (session.Status == FocusStatus.Paused)
        {
            extended = session with
            {
                RemainingWhenPaused = session.RemainingWhenPaused.Add(extension),
                UpdatedUtc = now,
            };
        }
        else
        {
            throw InvalidTransition("extend", session.Status);
        }

        if (!await _repository.TryCompareAndSetAsync(session, extended, cancellationToken))
        {
            throw TransitionConflict("extend");
        }

        return ToSnapshot(extended, now);
    }

    public async Task<bool> CompleteExpiredAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetRequiredAsync(id, cancellationToken);
        if (session.Status != FocusStatus.Running || session.EndsUtc is null)
        {
            return false;
        }

        var now = UtcNow();
        if (session.EndsUtc > now)
        {
            return false;
        }

        var completed = session with
        {
            Status = FocusStatus.Completed,
            UpdatedUtc = now,
        };
        return await _repository.TryCompareAndSetAsync(session, completed, cancellationToken);
    }

    public async Task<bool> CompleteExpiredAsync(CancellationToken cancellationToken)
    {
        var active = await _repository.GetActiveAsync(cancellationToken);
        return active is not null && await CompleteExpiredAsync(active.Id, cancellationToken);
    }

    public async Task<FocusSnapshot> EndAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await GetRequiredAsync(id, cancellationToken);
        if (session.Status is not (FocusStatus.Running or FocusStatus.Paused))
        {
            throw InvalidTransition("end", session.Status);
        }

        var now = UtcNow();
        var ended = session with
        {
            Status = FocusStatus.EndedEarly,
            UpdatedUtc = now,
        };
        if (!await _repository.TryCompareAndSetAsync(session, ended, cancellationToken))
        {
            throw TransitionConflict("end");
        }

        return ToSnapshot(ended, now);
    }

    private async Task<FocusSession> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await _repository.GetAsync(id, cancellationToken);
        return session ?? throw new KeyNotFoundException("alala focus session not found");
    }

    private DateTimeOffset UtcNow() => _clock.UtcNow.ToUniversalTime();

    private static TimeSpan RemainingForRunning(FocusSession session, DateTimeOffset now)
    {
        if (session.EndsUtc is null)
        {
            throw new InvalidOperationException("wait this focus session has no end time");
        }

        var remaining = session.EndsUtc.Value - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static FocusSnapshot ToSnapshot(FocusSession session, DateTimeOffset now) =>
        new(
            session.Id,
            session.TaskId,
            session.Status,
            session.Status == FocusStatus.Paused
                ? session.RemainingWhenPaused
                : session.Status == FocusStatus.Running
                    ? RemainingForRunning(session, now)
                    : TimeSpan.Zero);

    private static void EnsureStatus(FocusSession session, FocusStatus expected, string transition)
    {
        if (session.Status != expected)
        {
            throw InvalidTransition(transition, session.Status);
        }
    }

    private static InvalidOperationException InvalidTransition(string transition, FocusStatus status) =>
        new($"aiyo cant {transition} focus session in {status}");

    private static InvalidOperationException TransitionConflict(string transition) =>
        new("oh no that session changed before saving");
}
