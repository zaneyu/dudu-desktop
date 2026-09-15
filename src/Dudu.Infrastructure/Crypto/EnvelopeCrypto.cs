using System.Buffers.Text;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dudu.Core;

namespace Dudu.Infrastructure.Crypto;

/// <summary>
/// Raised when an <see cref="EncryptedEnvelope"/> or its decrypted payload does not match the
/// version-one wire contract. Distinct from <see cref="CryptographicException"/>, which is
/// reserved for authentication-tag failures raised natively by <see cref="AesGcm"/>.
/// </summary>
public sealed class EnvelopeValidationException : Exception
{
    public EnvelopeValidationException(string message)
        : base(message)
    {
    }

    public EnvelopeValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Decrypts and validates version-one envelopes against the wire contract shared with the
/// relay's TypeScript implementation (see protocol/README.md). Byte decoding and every
/// validation rule live here so callers never have to re-derive the contract themselves.
/// </summary>
/// <remarks>
/// TRUST MODEL (no sender authentication): AES-GCM proves an envelope was encrypted for this
/// desktop by someone holding its public key and that it was not modified afterwards, but
/// anyone can hold the public key — the relay never vouches for who sent a message. Every
/// payload field is therefore untrusted until validated below (kind/reaction allow-lists, text
/// and size ceilings, createdUtc window), and callers must never act on envelope content beyond
/// storing and displaying the note.
/// </remarks>
public static class EnvelopeCrypto
{
    public const string HkdfInfo = "DuduDesktop:message:v1";

    private const int HkdfSaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MinimumCiphertextLength = TagLength + 1;
    private const int MaximumCiphertextLength = 6144;
    private const int MaximumPayloadUtf8Bytes = 4096;
    private const int MaximumTextScalarValues = 2000;
    private const int MaximumCreatedUtcSkewMinutes = 5;

    /// <summary>
    /// The oldest createdUtc still accepted. Mirrors the relay's 30-day ciphertext retention:
    /// anything older cannot be a fresh delivery, only a replay or a long-delayed redelivery.
    /// </summary>
    private const int MaximumCreatedUtcAgeDays = 30;

    private static readonly Regex MessageIdPattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedReactions = new(StringComparer.Ordinal)
    {
        "none", "wave", "heart", "hug", "celebrate",
    };

    public static RemoteMessagePayload Decrypt(
        EncryptedEnvelope envelope,
        ReadOnlySpan<byte> pkcs8PrivateKey,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ProtocolVersion != ProductInfo.ProtocolVersion)
        {
            throw new EnvelopeValidationException(
                $"Unsupported protocolVersion '{envelope.ProtocolVersion}'.");
        }

        if (!MessageIdPattern.IsMatch(envelope.MessageId))
        {
            throw new EnvelopeValidationException("messageId is not a canonical lowercase UUID.");
        }

        if (!DateTimeOffset.TryParse(
                envelope.CreatedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var createdUtc))
        {
            throw new EnvelopeValidationException("createdUtc is not a parsable timestamp.");
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        if (createdUtc > now.AddMinutes(MaximumCreatedUtcSkewMinutes))
        {
            throw new EnvelopeValidationException("createdUtc is too far in the future.");
        }

        // The relay keeps ciphertext until max(createdUtc, deliverAfterUtc) + 30 days and lets a
        // note be scheduled up to 30 days after creation, so age is measured from when the note
        // became due. deliverAfterUtc is bound into the AAD, so the relay cannot stretch it; it is
        // still clamped to createdUtc + 30 days so a forged envelope cannot extend its own window.
        var dueUtc = createdUtc;
        if (!string.IsNullOrEmpty(envelope.DeliverAfterUtc)
            && DateTimeOffset.TryParse(
                envelope.DeliverAfterUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deliverAfterUtc)
            && deliverAfterUtc > createdUtc)
        {
            var latestSchedule = createdUtc.AddDays(MaximumCreatedUtcAgeDays);
            dueUtc = deliverAfterUtc < latestSchedule ? deliverAfterUtc : latestSchedule;
        }

        if (dueUtc < now.AddDays(-MaximumCreatedUtcAgeDays))
        {
            throw new EnvelopeValidationException(
                $"The note has been due for longer than the {MaximumCreatedUtcAgeDays}-day retention window.");
        }

        var salt = DecodeBase64Url(envelope.HkdfSalt, "hkdfSalt");
        if (salt.Length != HkdfSaltLength)
        {
            throw new EnvelopeValidationException($"hkdfSalt must be {HkdfSaltLength} bytes.");
        }

        var nonce = DecodeBase64Url(envelope.Nonce, "nonce");
        if (nonce.Length != NonceLength)
        {
            throw new EnvelopeValidationException($"nonce must be {NonceLength} bytes.");
        }

        var ciphertextAndTag = DecodeBase64Url(envelope.Ciphertext, "ciphertext");
        if (ciphertextAndTag.Length < MinimumCiphertextLength
            || ciphertextAndTag.Length > MaximumCiphertextLength)
        {
            throw new EnvelopeValidationException(
                $"ciphertext must be between {MinimumCiphertextLength} and {MaximumCiphertextLength} bytes.");
        }

        // Cheap length checks above run before any EC work, so malformed junk is rejected without
        // a public-key import or an ECDH agreement.
        var ephemeralPublicKeyBytes = DecodeBase64Url(envelope.EphemeralPublicKey, "ephemeralPublicKey");
        using var ephemeralPublicKey = ECDiffieHellman.Create();
        try
        {
            ephemeralPublicKey.ImportSubjectPublicKeyInfo(ephemeralPublicKeyBytes, out _);
        }
        catch (CryptographicException exception)
        {
            throw new EnvelopeValidationException(
                "ephemeralPublicKey is not a valid P-256 SPKI key.", exception);
        }

        using var recipient = ECDiffieHellman.Create();
        recipient.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);

        // Key material below is zeroed on every exit (success or failure) so a decrypted
        // plaintext, the message key, and the raw ECDH secret never linger on the managed heap
        // past this call. (The returned payload strings are caller-owned and unavoidably live on.)
        byte[]? sharedSecret = null;
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            sharedSecret = recipient.DeriveRawSecretAgreement(ephemeralPublicKey.PublicKey);

            key = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                outputLength: 32,
                salt,
                Encoding.UTF8.GetBytes(HkdfInfo));

            var additionalAuthenticatedData = BuildAdditionalAuthenticatedData(envelope);
            var ciphertext = ciphertextAndTag.AsSpan(0, ciphertextAndTag.Length - TagLength);
            var tag = ciphertextAndTag.AsSpan(ciphertextAndTag.Length - TagLength);
            plaintext = new byte[ciphertext.Length];

            using var aesGcm = new AesGcm(key, TagLength);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, additionalAuthenticatedData);

            if (plaintext.Length > MaximumPayloadUtf8Bytes)
            {
                throw new EnvelopeValidationException(
                    $"Decrypted payload exceeds {MaximumPayloadUtf8Bytes} UTF-8 bytes.");
            }

            RemoteMessagePayload payload;
            try
            {
                payload = JsonSerializer.Deserialize(plaintext, EnvelopeJsonContext.Default.RemoteMessagePayload)
                    ?? throw new EnvelopeValidationException("Decrypted payload is null.");
            }
            catch (JsonException exception)
            {
                throw new EnvelopeValidationException("Decrypted payload is not valid JSON.", exception);
            }

            // System.Text.Json does not enforce nullable reference annotations, so every one of
            // these can still arrive as JSON null (or be absent) on a well-formed envelope. Each
            // must be rejected as a validation failure here, before it reaches a HashSet lookup or
            // a string member below and throws an exception type the caller does not expect.
            if (payload.Kind is null || payload.Text is null || payload.Reaction is null)
            {
                throw new EnvelopeValidationException("Decrypted payload is missing kind, text, or reaction.");
            }

            if (payload.Kind != "note")
            {
                throw new EnvelopeValidationException($"Unsupported payload kind '{payload.Kind}'.");
            }

            if (!AllowedReactions.Contains(payload.Reaction))
            {
                throw new EnvelopeValidationException($"Unknown reaction '{payload.Reaction}'.");
            }

            if (payload.Text.EnumerateRunes().Count() > MaximumTextScalarValues)
            {
                throw new EnvelopeValidationException(
                    $"text exceeds {MaximumTextScalarValues} Unicode scalar values.");
            }

            return payload;
        }
        finally
        {
            if (sharedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }

            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] BuildAdditionalAuthenticatedData(EncryptedEnvelope envelope)
    {
        var deliverAfterUtc = envelope.DeliverAfterUtc ?? string.Empty;
        var aad = string.Create(
            CultureInfo.InvariantCulture,
            $"{envelope.ProtocolVersion}|{envelope.MessageId}|{envelope.CreatedUtc}|{deliverAfterUtc}");
        return Encoding.UTF8.GetBytes(aad);
    }

    private static byte[] DecodeBase64Url(string value, string fieldName)
    {
        try
        {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException exception)
        {
            throw new EnvelopeValidationException($"{fieldName} is not valid Base64URL.", exception);
        }
    }
}
