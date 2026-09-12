using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

public sealed class OfflinePairingService : IPairingService
{
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
}
