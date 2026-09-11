using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ITaskRepository
{
    Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken);

    Task SaveAsync(TaskItem task, CancellationToken cancellationToken);
}
