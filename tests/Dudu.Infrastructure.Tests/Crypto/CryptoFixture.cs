using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dudu.Infrastructure.Crypto;

namespace Dudu.Infrastructure.Tests.Crypto;

/// <summary>
/// A test-only, from-scratch implementation of the wire encryption contract defined by
/// <see cref="EnvelopeCrypto"/>. It exists so tamper tests can build deliberately malformed
/// envelopes without going through (and thus being constrained by) the production decrypt path.
/// It must NOT be reused by any production code.
/// </summary>
internal static class CryptoFixture
{
    private const int HkdfSaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static EncryptedEnvelope EncryptFor(
        byte[] recipientSpkiPublicKey,
        string text,
        string? messageId = null,
        DateTimeOffset? createdUtc = null,
        string? deliverAfterUtc = null,
        string reaction = "none")
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteMessagePayload("note", text, reaction),
            EnvelopeJsonContext.Default.RemoteMessagePayload);

        return EncryptRawPayloadFor(
            recipientSpkiPublicKey,
            payloadBytes,
            messageId,
            createdUtc,
            deliverAfterUtc);
    }

    public static EncryptedEnvelope EncryptRawPayloadFor(
        byte[] recipientSpkiPublicKey,
        byte[] payloadBytes,
        string? messageId = null,
        DateTimeOffset? createdUtc = null,
        string? deliverAfterUtc = null)
    {
        using var recipientPublic = ECDiffieHellman.Create();
        recipientPublic.ImportSubjectPublicKeyInfo(recipientSpkiPublicKey, out _);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sharedSecret = ephemeral.DeriveRawSecretAgreement(recipientPublic.PublicKey);

        var salt = RandomNumberGenerator.GetBytes(HkdfSaltLength);
        var info = Encoding.UTF8.GetBytes(EnvelopeCrypto.HkdfInfo);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, info);

        var id = messageId ?? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var created = (createdUtc ?? DateTimeOffset.UtcNow).ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var aad = Encoding.UTF8.GetBytes(
            $"1|{id}|{created}|{deliverAfterUtc ?? string.Empty}");

        var ciphertext = new byte[payloadBytes.Length];
        var tag = new byte[TagLength];
        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Encrypt(nonce, payloadBytes, ciphertext, tag, aad);

        var ciphertextAndTag = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(ciphertextAndTag, 0);
        tag.CopyTo(ciphertextAndTag, ciphertext.Length);

        return new EncryptedEnvelope(
            ProtocolVersion: 1,
            MessageId: id,
            CreatedUtc: created,
            DeliverAfterUtc: deliverAfterUtc,
            EphemeralPublicKey: Base64Url.EncodeToString(ephemeral.ExportSubjectPublicKeyInfo()),
            HkdfSalt: Base64Url.EncodeToString(salt),
            Nonce: Base64Url.EncodeToString(nonce),
            Ciphertext: Base64Url.EncodeToString(ciphertextAndTag));
    }
}
