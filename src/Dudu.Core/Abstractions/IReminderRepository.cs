using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IReminderRepository
{
    Task<IReadOnlyList<Reminder>> LoadDueAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    Task RecordOccurrencesAndAdvanceAsync(
        Reminder reminder,
        IReadOnlyList<ReminderOccurrence> occurrences,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken);
}
