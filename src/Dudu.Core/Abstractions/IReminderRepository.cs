using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IReminderRepository
{
    /// <summary>Lists reminders for settings management, including disabled reminders.</summary>
    Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<Reminder>>(new NotSupportedException(
            "This reminder repository does not support settings management."));

    Task<IReadOnlyList<Reminder>> LoadDueAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    Task<bool> RecordOccurrencesAndAdvanceAsync(
        Reminder reminder,
        IReadOnlyList<ReminderOccurrence> occurrences,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken);
}
