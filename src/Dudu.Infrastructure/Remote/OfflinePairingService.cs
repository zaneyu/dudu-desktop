using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Crypto;

namespace Dudu.Infrastructure.Remote;

public sealed class OfflinePairingService : IPairingService
{
    private readonly ISecretStore _secretStore;

    public OfflinePairingService(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

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

    /// <summary>
    /// Review M4: the default <see cref="IPairingService.ForgetPairingAsync"/> throws
    /// <see cref="NotSupportedException"/> with a developer-facing message that the Connection
    /// page's confirmation flow would otherwise show verbatim. This implementation is registered
    /// exactly when no relay is configured, so there is normally no local pairing to forget --
    /// but a relay could have been configured once and then removed from settings, leaving stray
    /// secrets behind from before this build started using the offline implementation. Best-effort
    /// clean those up too (ignoring any failure, matching <see cref="RemoteSyncService.ForgetPairingLocallyAsync"/>'s
    /// own local-only recovery intent) instead of leaving them, and instead of surfacing raw
    /// developer English for what is, from here, always a harmless no-op.
    /// </summary>
    public async Task ForgetPairingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await TryDeleteSecretAsync(RelaySecretKeys.DeviceId, cancellationToken);
        await TryDeleteSecretAsync(RelaySecretKeys.DesktopToken, cancellationToken);
        await TryDeleteSecretAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);
        await TryDeleteSecretAsync(DesktopKeyService.SecretStoreKey, cancellationToken);
    }

    private async Task TryDeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _secretStore.DeleteAsync(key, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort: nothing this offline implementation does depends on these secrets
            // being gone, so a delete failure here (or the secret never having existed) must
            // never surface as an error.
        }
    }
}
