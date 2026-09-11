using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IReminderDueSink
{
    Task NotifyAsync(
        ReminderOccurrence occurrence,
        CancellationToken cancellationToken);
}
