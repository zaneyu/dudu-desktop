using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ICountdownRepository
{
    Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(Countdown countdown, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}
