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
    DateTimeOffset? DeliverAfterUtc,
    DateTimeOffset ReceivedUtc)
{
    public RemoteEnvelope(
        string messageId,
        byte[] ciphertext,
        DateTimeOffset receivedUtc)
        : this(messageId, ciphertext, null, null, null, null, receivedUtc)
    {
    }
}
