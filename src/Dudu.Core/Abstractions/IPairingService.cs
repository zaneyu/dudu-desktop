namespace Dudu.Core.Abstractions;

public enum PairingAvailability
{
    Offline,
    Available,
    NeedsRepair,
}

public sealed record PairingCodeResult(
    PairingAvailability Availability,
    string? Code,
    DateTimeOffset? ExpiresUtc)
{
    public static PairingCodeResult Offline { get; } =
        new(PairingAvailability.Offline, null, null);
}

public interface IPairingService
{
    Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default);

    Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default);

    Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default);

    Task DeleteRemoteDeviceAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default);
}
