using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IFocusSessionRepository
{
    Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken);

    Task<bool> TryCreateActiveAsync(
        FocusSession session,
        CancellationToken cancellationToken);

    Task<bool> TryCompareAndSetAsync(
        FocusSession expected,
        FocusSession replacement,
        CancellationToken cancellationToken);

    Task SaveAsync(FocusSession session, CancellationToken cancellationToken);
}
