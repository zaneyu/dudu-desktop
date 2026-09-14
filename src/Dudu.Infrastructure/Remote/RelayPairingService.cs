using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Thin <see cref="IPairingService"/> adapter over <see cref="RemoteSyncService"/>, registered
/// in place of <see cref="OfflinePairingService"/> once a relay base URL is configured. Member
/// names differ from <see cref="RemoteSyncService"/>'s own (task-shaped) API only to match the
/// interface every feature view model already depends on.
/// </summary>
public sealed class RelayPairingService : IPairingService
{
    private readonly RemoteSyncService _sync;

    public RelayPairingService(RemoteSyncService sync)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
    }

    public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) =>
        _sync.GetStateAsync(cancellationToken);

    public PairingStatusReason StatusReason => _sync.StatusReason;

    public async Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sync.CreatePairingCodeAsync(cancellationToken);
        }
        catch (RelayUnauthorizedException)
        {
            return new PairingCodeResult(PairingAvailability.NeedsRepair, null, null);
        }
    }

    public Task<int> GetSessionCountAsync(CancellationToken cancellationToken = default) =>
        _sync.GetActiveSenderSessionCountAsync(cancellationToken);

    // The relay exposes no per-session listing endpoint, only an aggregate count
    // (GetSessionCountAsync, above) and a bulk disconnect (DisconnectSenderSessionsAsync,
    // below). ListSessionsAsync therefore always returns an empty list so callers fall back to
    // the aggregate count instead of seeing a NotSupportedException.
    public Task<IReadOnlyList<PairingSessionSummary>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PairingSessionSummary>>(Array.Empty<PairingSessionSummary>());
    }

    // RevokeSessionAsync intentionally falls back to IPairingService's default
    // (NotSupportedException): the relay has no per-session revocation endpoint.

    public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) =>
        _sync.DisconnectSendersAsync(cancellationToken);

    public Task DeleteRemoteDeviceAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        _sync.RevokeDeviceAsync(cancellationToken);
}
