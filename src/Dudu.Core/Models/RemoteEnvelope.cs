namespace Dudu.Core.Models;

/// <summary>
/// Ciphertext and its protocol metadata. Plaintext is intentionally absent.
/// </summary>
public sealed record RemoteEnvelope(
    string MessageId,
    byte[] Ciphertext,
    byte[]? EphemeralPublicKey,
    byte[]? Nonce,
    byte[]? AuthenticationTag,
    string? DeliverAfterUtc,
    DateTimeOffset ReceivedUtc)
{
    /// <summary>
    /// The HKDF salt used to derive the message key. Nullable because it was added after the
    /// initial schema (migration 0003); rows written before then have no salt on file.
    /// </summary>
    public byte[]? HkdfSalt { get; init; }

    /// <summary>
    /// The original wire "createdUtc" string, verbatim. This (not any reparsed/reformatted
    /// value) is required to reconstruct the AES-GCM additional authenticated data during a
    /// later reveal, since envelope decryption authenticates the exact bytes the sender wrote.
    /// Nullable because it was added after the initial schema (migration 0003); rows written
    /// before then have no created-at on file.
    /// </summary>
    public string? CreatedUtc { get; init; }

    public RemoteEnvelope(
        string messageId,
        byte[] ciphertext,
        DateTimeOffset receivedUtc)
        : this(messageId, ciphertext, null, null, null, null, receivedUtc)
    {
    }
}
