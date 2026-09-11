using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IProfileRepository
{
    Task<Profile?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(Profile profile, CancellationToken cancellationToken);
}
