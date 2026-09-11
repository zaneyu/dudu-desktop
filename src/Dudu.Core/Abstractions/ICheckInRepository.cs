using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ICheckInRepository
{
    Task SaveAsync(
        MoodCheckIn checkIn,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}
