using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data;

/// <summary>SQLite-backed cross-repository operation behind the love notes reveal:
/// keep the revealed remote note and consume its envelope in one unit of work.</summary>
public sealed class CompanionFeatureTransactionService : ICompanionFeatureTransactions
{
    private readonly IAppUnitOfWork _unitOfWork;
    private readonly Func<string, CancellationToken, Task>? _faultInjector;

    public CompanionFeatureTransactionService(IAppUnitOfWork unitOfWork)
        : this(unitOfWork, null)
    {
    }

    internal CompanionFeatureTransactionService(
        IAppUnitOfWork unitOfWork,
        Func<string, CancellationToken, Task>? faultInjector)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _faultInjector = faultInjector;
    }

    public Task SaveRemoteNoteAndConsumeEnvelopeAsync(
        LocalLoveNote note,
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _unitOfWork.ExecuteAsync(async (context, token) =>
        {
            await context.LocalNotes.SaveToJarAsync(note, token);
            await InjectFaultAsync("after-note-save", token);
            if (!await context.RemoteEnvelopes.TryConsumeAsync(
                messageId,
                processedUtc.ToUniversalTime(),
                token))
            {
                throw new InvalidOperationException(
                    "That remote note was already consumed or is no longer available.");
            }
            await InjectFaultAsync("after-envelope-consume", token);
        }, cancellationToken);
    }

    private Task InjectFaultAsync(string point, CancellationToken cancellationToken) =>
        _faultInjector?.Invoke(point, cancellationToken) ?? Task.CompletedTask;
}
