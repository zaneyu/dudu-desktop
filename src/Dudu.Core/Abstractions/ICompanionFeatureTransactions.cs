using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

/// <summary>
/// Atomic persistence operations whose invariants span more than one local
/// repository.
/// </summary>
public interface ICompanionFeatureTransactions
{
    Task SavePreferencesAndDefaultRemindersAsync(
        Preferences preferences,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone,
        CancellationToken cancellationToken = default);

    Task RestorePreferencesAndDefaultRemindersAsync(
        Preferences preferences,
        IReadOnlyList<Reminder> previousDefaultReminders,
        CancellationToken cancellationToken = default);

    Task SaveRemoteNoteAndConsumeEnvelopeAsync(
        LocalLoveNote note,
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken = default);
}
