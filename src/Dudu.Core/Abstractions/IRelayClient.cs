namespace Dudu.Core.Abstractions;

/// <summary>
/// A wire-level envelope returned by the relay's poll endpoint. Every binary field stays
/// Base64URL-encoded here (as received) because Core has no cryptography dependency; decoding
/// and decryption are an Infrastructure concern.
/// </summary>
public sealed record RelayEnvelope(
    int ProtocolVersion,
    string MessageId,
    string CreatedUtc,
    string? DeliverAfterUtc,
    string EphemeralPublicKey,
    string HkdfSalt,
    string Nonce,
    string Ciphertext);

/// <summary>Result of a successful device registration.</summary>
public sealed record RelayRegistrationResult(
    string DeviceId,
    string DesktopToken,
    string PairingCode,
    DateTimeOffset PairingCodeExpiresUtc);

/// <summary>Non-secret metadata about the currently registered device.</summary>
public sealed record RelayDeviceInfo(
    DateTimeOffset CreatedUtc,
    string PublicKeyFingerprint,
    int ActiveSenderSessions);

/// <summary>A freshly minted pairing code and its expiry.</summary>
public sealed record RelayPairingCode(string Code, DateTimeOffset ExpiresUtc);

/// <summary>
/// Base type for every failure this client and the service built on top of it can raise.
/// Never carries tokens, keys, ciphertext, plaintext, or pairing codes in its message.
/// </summary>
public class RemoteSyncException : Exception
{
    public RemoteSyncException(string message)
        : base(message)
    {
    }

    public RemoteSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The relay rejected this device's bearer token (HTTP 401).</summary>
public sealed class RelayUnauthorizedException : RemoteSyncException
{
    public RelayUnauthorizedException(string message = "The relay rejected this device's credentials.")
        : base(message)
    {
    }
}

/// <summary>The relay could not be reached, timed out, or returned a server error (5xx).</summary>
public sealed class RelayUnavailableException : RemoteSyncException
{
    public RelayUnavailableException(string message)
        : base(message)
    {
    }

    public RelayUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The relay responded but with an unexpected shape: a non-401/5xx error status, or a body that
/// did not deserialize into the expected contract.
/// </summary>
public sealed class RelayProtocolException : RemoteSyncException
{
    public RelayProtocolException(string message)
        : base(message)
    {
    }

    public RelayProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The desktop's authenticated surface onto the relay Worker. Every member takes a
/// <see cref="CancellationToken"/> and never logs or echoes tokens, keys, ciphertext, plaintext,
/// or pairing codes.
/// </summary>
public interface IRelayClient
{
    Task<RelayRegistrationResult> RegisterAsync(string publicKeySpki, CancellationToken cancellationToken);

    Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken);

    Task<RelayPairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken);

    Task<string> RotateKeyAsync(string publicKeySpki, CancellationToken cancellationToken);

    Task DeleteDeviceAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RelayEnvelope>> PollAsync(CancellationToken cancellationToken);

    Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken);
}
