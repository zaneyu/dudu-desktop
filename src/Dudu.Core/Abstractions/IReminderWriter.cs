using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IReminderWriter
{
    Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default);
}
