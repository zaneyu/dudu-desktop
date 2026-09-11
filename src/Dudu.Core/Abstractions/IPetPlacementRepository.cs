using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IPetPlacementRepository
{
    Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken);

    Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken);

    Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken);
}
