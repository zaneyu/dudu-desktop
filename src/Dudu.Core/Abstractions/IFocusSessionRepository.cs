using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IFocusSessionRepository
{
    Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Lists completed or ended focus sessions; running and paused sessions are excluded.</summary>
    Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<FocusSession>>(new NotSupportedException(
            "This focus repository does not support focus history."));

    Task<bool> TryCreateActiveAsync(
        FocusSession session,
        CancellationToken cancellationToken);

    Task<bool> TryCompareAndSetAsync(
        FocusSession expected,
        FocusSession replacement,
        CancellationToken cancellationToken);

    Task SaveAsync(FocusSession session, CancellationToken cancellationToken);
}
