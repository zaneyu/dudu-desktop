using System.Text.Json.Serialization;

namespace Dudu.Infrastructure.Crypto;

/// <summary>
/// The version-one wire envelope exchanged between the browser sender and the desktop
/// companion. Every binary field is unpadded Base64URL; see protocol/README.md for the
/// full wire contract this record mirrors byte-for-byte with its TypeScript counterpart.
/// </summary>
public sealed record EncryptedEnvelope(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("deliverAfterUtc")] string? DeliverAfterUtc,
    [property: JsonPropertyName("ephemeralPublicKey")] string EphemeralPublicKey,
    [property: JsonPropertyName("hkdfSalt")] string HkdfSalt,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ciphertext")] string Ciphertext);

/// <summary>
/// The decrypted note payload carried inside an <see cref="EncryptedEnvelope"/>.
/// </summary>
public sealed record RemoteMessagePayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reaction")] string Reaction);

/// <summary>
/// Source-generated serialization context for the wire types above. Reused by
/// Dudu.CryptoInterop and (per Task 20) the desktop relay client, so the JSON shape stays
/// in exactly one place.
/// </summary>
[JsonSerializable(typeof(EncryptedEnvelope))]
[JsonSerializable(typeof(RemoteMessagePayload))]
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class EnvelopeJsonContext : JsonSerializerContext
{
}
