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
}
