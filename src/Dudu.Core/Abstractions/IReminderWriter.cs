using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IReminderWriter
{
    Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default);

    Task DeleteAsync(string reminderId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This reminder writer does not support reminder deletion."));
}
