namespace Dudu.Core.Abstractions;

public enum PairingAvailability
{
    Offline,
    Available,
    NeedsRepair,
}

/// <summary>
/// Why pairing is in its current <see cref="PairingAvailability"/>, when the coarse state alone
/// would leave the user staring at a generic "offline" line with no idea what to do. The
/// Connection page turns this into its status text, so every member must map to a sentence a
/// non-technical user can act on.
/// </summary>
public enum PairingStatusReason
{
    /// <summary>Nothing to add beyond <see cref="PairingAvailability"/> itself.</summary>
    None,

    /// <summary>No relay base URL is configured, so the app runs fully local (review I9).</summary>
    RelayNotConfigured,

    /// <summary>
    /// The relay answered with something this build cannot read — an oversized page or a
    /// malformed body. Retried on a long backoff rather than tight-looping (review C1).
    /// </summary>
    RelayProtocolError,
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

    /// <summary>
    /// The latest reason behind <see cref="GetStateAsync"/>'s answer, for implementations that
    /// can distinguish one. Defaults to <see cref="PairingStatusReason.None"/> so existing
    /// implementations need not change.
    /// </summary>
    PairingStatusReason StatusReason => PairingStatusReason.None;

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
