using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ILocalNoteRepository
{
    Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(
        CancellationToken cancellationToken);

    Task<int> CountUnsolicitedShownAsync(
        DateOnly localDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(
        int count,
        CancellationToken cancellationToken);

    Task RecordShownAsync(
        string noteId,
        DateTimeOffset shownUtc,
        bool unsolicited,
        CancellationToken cancellationToken);
}
