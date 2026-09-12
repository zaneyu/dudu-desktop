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

/// <summary>
/// An opaque handle for a paired sender session. It intentionally omits browser,
/// identity, activity, and browsing-history metadata.
/// </summary>
public sealed record PairingSessionSummary(string SessionId);

/// <summary>Result returned by a destructive relay operation. A completed
/// task is not success when the configured relay cannot perform it.</summary>
public sealed record PairingOperationResult(bool Completed, string? ErrorMessage = null)
{
    public static PairingOperationResult Unavailable(string message) => new(false, message);
    public static PairingOperationResult Success { get; } = new(true);
}

public interface IPairingService
{
    Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default);

    Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default);

    Task<int> GetSessionCountAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<int>(new NotSupportedException(
            "This pairing service does not support session management."));

    Task<IReadOnlyList<PairingSessionSummary>> ListSessionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<PairingSessionSummary>>(new NotSupportedException(
            "This pairing service does not support session management."));

    Task RevokeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Task.FromException(new NotSupportedException(
            "This pairing service does not support session management."));
    }

    Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default);

    Task DeleteRemoteDeviceAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    async Task<PairingOperationResult> RevokeSessionsWithResultAsync(
        CancellationToken cancellationToken = default)
    {
        if (await GetStateAsync(cancellationToken) == PairingAvailability.Offline)
        {
            return PairingOperationResult.Unavailable("Pairing is unavailable while the relay is offline.");
        }

        await DisconnectSenderSessionsAsync(cancellationToken);
        return PairingOperationResult.Success;
    }

    async Task<PairingOperationResult> DeleteRemoteDeviceWithResultAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        if (await GetStateAsync(cancellationToken) == PairingAvailability.Offline)
        {
            return PairingOperationResult.Unavailable("Remote-device deletion is unavailable while the relay is offline.");
        }

        await DeleteRemoteDeviceAsync(deviceId, cancellationToken);
        return PairingOperationResult.Success;
    }
}
