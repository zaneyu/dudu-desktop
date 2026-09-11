using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface IPreferencesRepository
{
    Task<Preferences?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(Preferences preferences, CancellationToken cancellationToken);
}
