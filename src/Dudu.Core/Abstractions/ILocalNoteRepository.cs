using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ILocalNoteRepository
{
    /// <summary>Lists every note in the local jar, including notes currently disabled from ambient display.</summary>
    Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<LocalLoveNote>>(new NotSupportedException(
            "This local-note repository does not support jar management."));

    Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(
        CancellationToken cancellationToken);

    /// <summary>Explicitly saves a note to the local-only love-note jar.</summary>
    Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This local-note repository does not support jar management."));

    Task DeleteAsync(string noteId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This local-note repository does not support jar management."));

    Task<int> CountUnsolicitedShownAsync(
        DateOnly localDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(
        int count,
        CancellationToken cancellationToken);

    Task<bool> TryRecordShownAsync(
        string noteId,
        DateTimeOffset shownUtc,
        DateOnly localDate,
        int dailyLimit,
        bool unsolicited,
        CancellationToken cancellationToken);
}
