using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.Tasks;

public sealed class TaskService
{
    private const int MaxTitleScalars = 120;
    private const int MaxNotesScalars = 2_000;

    private readonly ITaskRepository _repository;
    private readonly IClock _clock;

    public TaskService(ITaskRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<TaskItem> CreateAsync(
        string title,
        string? notes,
        DateTimeOffset? dueUtc,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = ValidateTitle(title);
        ValidateNotes(notes);
        var now = UtcNow();
        var task = new TaskItem(
            Guid.NewGuid(),
            normalizedTitle,
            notes,
            NormalizeUtc(dueUtc),
            false,
            now,
            now,
            null);

        await _repository.SaveAsync(task, cancellationToken);
        return task;
    }

    public Task<TaskItem> CreateAsync(
        string title,
        string? notes,
        CancellationToken cancellationToken) =>
        CreateAsync(title, notes, null, cancellationToken);

    public Task<TaskItem> CreateAsync(
        string title,
        CancellationToken cancellationToken) =>
        CreateAsync(title, null, null, cancellationToken);

    public async Task<TaskItem> UpdateAsync(
        Guid id,
        string title,
        string? notes,
        DateTimeOffset? dueUtc,
        CancellationToken cancellationToken)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        var normalizedTitle = ValidateTitle(title);
        ValidateNotes(notes);
        var updated = task with
        {
            Title = normalizedTitle,
            Notes = notes,
            DueUtc = NormalizeUtc(dueUtc),
            UpdatedUtc = UtcNow(),
        };

        if (!await _repository.TryCompareAndSetAsync(task, updated, cancellationToken))
        {
            throw new InvalidOperationException("oh no that task changed before saving try again");
        }
        return updated;
    }

    public Task<TaskItem> UpdateAsync(
        Guid id,
        string title,
        string? notes,
        CancellationToken cancellationToken) =>
        UpdateWithoutChangingDueUtcAsync(id, title, notes, cancellationToken);

    private async Task<TaskItem> UpdateWithoutChangingDueUtcAsync(
        Guid id,
        string title,
        string? notes,
        CancellationToken cancellationToken)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        var normalizedTitle = ValidateTitle(title);
        ValidateNotes(notes);
        var updated = task with
        {
            Title = normalizedTitle,
            Notes = notes,
            UpdatedUtc = UtcNow(),
        };

        if (!await _repository.TryCompareAndSetAsync(task, updated, cancellationToken))
        {
            throw new InvalidOperationException("oh no that task changed before saving try again");
        }
        return updated;
    }

    public async Task<TaskItem> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        if (task.IsCompleted)
        {
            return task;
        }

        var now = UtcNow();
        var completed = task with
        {
            IsCompleted = true,
            CompletedUtc = now,
            UpdatedUtc = now,
        };

        if (!await _repository.TryCompareAndSetAsync(task, completed, cancellationToken))
        {
            throw new InvalidOperationException("oh no that task changed before finishing try again");
        }
        return completed;
    }

    public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) =>
        _repository.ListActiveAsync(cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await GetRequiredAsync(id, cancellationToken);
        await _repository.DeleteAsync(id, cancellationToken);
    }

    private async Task<TaskItem> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(id, cancellationToken);
        return task ?? throw new KeyNotFoundException("oh no that task is gone refresh and try again");
    }

    private DateTimeOffset UtcNow() => _clock.UtcNow.ToUniversalTime();

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? value) =>
        value?.ToUniversalTime();

    private static string ValidateTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("aiyo add a title first", nameof(title));
        }

        if (CountScalars(normalized) > MaxTitleScalars)
        {
            throw new ArgumentException(
                $"keep the title under {MaxTitleScalars} characters",
                nameof(title));
        }

        return normalized;
    }

    private static void ValidateNotes(string? notes)
    {
        if (notes is not null && CountScalars(notes) > MaxNotesScalars)
        {
            throw new ArgumentException(
                $"keep the notes under {MaxNotesScalars} characters",
                nameof(notes));
        }
    }

    private static int CountScalars(string value) => value.EnumerateRunes().Count();
}
