using Dudu.Core.Models;

namespace Dudu.Core.Reminders;

public static class ReminderOccurrencePolicy
{
    public static IReadOnlyList<ReminderOccurrence> Select(
        Reminder reminder,
        IReadOnlyList<ReminderOccurrence> occurrences,
        bool isCatchUp = true)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(occurrences);

        if (occurrences.Count == 0)
        {
            return Array.Empty<ReminderOccurrence>();
        }

        if (isCatchUp && reminder.MissedPolicy == MissedOccurrencePolicy.Skip)
        {
            return Array.Empty<ReminderOccurrence>();
        }

        return [occurrences[^1]];
    }
}

public sealed record ReminderReconciliation
{
    public ReminderReconciliation(
        IReadOnlyList<ReminderOccurrence> dueNow,
        DateTimeOffset? nextUtc)
    {
        ArgumentNullException.ThrowIfNull(dueNow);
        DueNow = Array.AsReadOnly(dueNow.ToArray());
        NextUtc = nextUtc;
    }

    public IReadOnlyList<ReminderOccurrence> DueNow { get; }

    public DateTimeOffset? NextUtc { get; }
}
