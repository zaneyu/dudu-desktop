using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

/// <summary>Local storage boundary for ciphertext received from the relay.</summary>
public interface IRemoteEnvelopeRepository
{
    Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken);

    Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken);

    Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken);

    Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken);

    async Task<bool> TryConsumeAsync(
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken)
    {
        if (!await TryMarkProcessedAsync(messageId, processedUtc, cancellationToken)) return false;
        await DeleteAsync(messageId, cancellationToken);
        return true;
    }

    Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken);

    Task DeleteAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Deletes unconsumed envelopes that have been deliverable for longer than
    /// <paramref name="retention"/>, bounding local ciphertext accumulation when the user never
    /// opens a note. Returns the number of envelopes removed. Processed-message deduplication rows
    /// are intentionally kept, so a pruned id can never resurrect as a "new" message.</summary>
    Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken);
}
