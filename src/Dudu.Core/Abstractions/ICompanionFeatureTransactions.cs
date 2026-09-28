using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

/// <summary>
/// Atomic persistence operations whose invariants span more than one local
/// repository.
/// </summary>
public interface ICompanionFeatureTransactions
{
    Task SaveRemoteNoteAndConsumeEnvelopeAsync(
        LocalLoveNote note,
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken = default);
}
