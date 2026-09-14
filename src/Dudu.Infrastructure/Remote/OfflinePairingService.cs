using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

public sealed class OfflinePairingService : IPairingService
{
    /// <summary>
    /// Review I9: this service is registered exactly when no relay base URL is configured, so
    /// "offline" here always has one specific cause. Saying so lets the Connection page explain
    /// it instead of showing a bare failure.
    /// </summary>
    public PairingStatusReason StatusReason => PairingStatusReason.RelayNotConfigured;

    public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PairingAvailability.Offline);
    }

    public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PairingCodeResult.Offline);
    }

    public Task<int> GetSessionCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<PairingSessionSummary>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PairingSessionSummary>>(Array.Empty<PairingSessionSummary>());
    }

    public Task RevokeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DeleteRemoteDeviceAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<PairingOperationResult> RevokeSessionsWithResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PairingOperationResult.Unavailable("Pairing is unavailable while the relay is offline."));
    }

    public Task<PairingOperationResult> DeleteRemoteDeviceWithResultAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PairingOperationResult.Unavailable("Remote-device deletion is unavailable while the relay is offline."));
    }
}
