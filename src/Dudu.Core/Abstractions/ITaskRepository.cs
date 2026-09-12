using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ITaskRepository
{
    Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Lists completed tasks for the local task history.</summary>
    Task<IReadOnlyList<TaskItem>> ListCompletedAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<TaskItem>>(new NotSupportedException(
            "This task repository does not support task history."));

    Task SaveAsync(TaskItem task, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This task repository does not support task deletion."));
}
