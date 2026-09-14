using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace Dudu.App.Hosting;

/// <summary>
/// Test-only fixture hook gated by the <c>DUDU_FIXTURE_NOTE</c> environment variable. When it is
/// exactly <c>"1"</c>, inserts one remote note through the same repository surface the real relay
/// sync path uses (<see cref="IRemoteEnvelopeRepository.TryInsertAsync"/>), so UI automation can
/// exercise "reveal a remote note" end to end without a live relay.
/// </summary>
/// <remarks>
/// The row is ciphertext-only by construction: <see cref="RemoteEnvelope"/> has no plaintext
/// field, and this class never bypasses <see cref="IRemoteEnvelopeRepository"/>. The note is
/// encrypted against this device's own real pairing public key (from <see cref="DesktopKeyService"/>)
/// using the exact ECDH P-256 + HKDF-SHA256 + AES-GCM wire contract <see cref="EnvelopeCrypto"/>
/// decrypts, so a normal reveal works unmodified. This reimplements the handful of primitive
/// calls locally rather than referencing the test-only
/// <c>Dudu.Infrastructure.Tests.Crypto.CryptoFixture</c> helper, which is explicitly documented
/// as not for production use and which this project cannot reference in any case.
/// </remarks>
internal static class FixtureRemoteNoteInstaller
{
    private const string EnvironmentVariableName = "DUDU_FIXTURE_NOTE";

    // Fixed so a relaunch during a UI test run is idempotent: the insert SQL is
    // "ON CONFLICT(message_id) DO NOTHING", so calling this again is a safe no-op.
    private const string FixtureMessageId = "00000000-0000-4000-8000-000000000001";

    private const int HkdfSaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static async Task InstallIfRequestedAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariableName) != "1")
        {
            return;
        }

        var keyService = services.GetRequiredService<DesktopKeyService>();
        var envelopes = services.GetRequiredService<IRemoteEnvelopeRepository>();
        var clock = services.GetRequiredService<IClock>();

        var keyMaterial = await keyService.GetOrCreateAsync(cancellationToken);
        try
        {
            var recipientPublicKey = Base64Url.DecodeFromChars(keyMaterial.PublicKeySpkiBase64Url);
            var wire = Encrypt(recipientPublicKey, clock.UtcNow);
            await envelopes.TryInsertAsync(ToStoredEnvelope(wire, clock.UtcNow), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
        }
    }

    private static EncryptedEnvelope Encrypt(byte[] recipientSpkiPublicKey, DateTimeOffset createdUtc)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteMessagePayload("note", "oki fixture note here for testing", "heart"),
            EnvelopeJsonContext.Default.RemoteMessagePayload);

        using var recipientPublic = ECDiffieHellman.Create();
        recipientPublic.ImportSubjectPublicKeyInfo(recipientSpkiPublicKey, out _);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sharedSecret = ephemeral.DeriveRawSecretAgreement(recipientPublic.PublicKey);

        var salt = RandomNumberGenerator.GetBytes(HkdfSaltLength);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            outputLength: 32,
            salt,
            Encoding.UTF8.GetBytes(EnvelopeCrypto.HkdfInfo));

        var createdUtcText = createdUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var additionalAuthenticatedData = Encoding.UTF8.GetBytes(
            $"{Dudu.Core.ProductInfo.ProtocolVersion}|{FixtureMessageId}|{createdUtcText}|");

        var ciphertext = new byte[payloadBytes.Length];
        var tag = new byte[TagLength];
        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Encrypt(nonce, payloadBytes, ciphertext, tag, additionalAuthenticatedData);

        var ciphertextAndTag = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(ciphertextAndTag, 0);
        tag.CopyTo(ciphertextAndTag, ciphertext.Length);

        return new EncryptedEnvelope(
            ProtocolVersion: Dudu.Core.ProductInfo.ProtocolVersion,
            MessageId: FixtureMessageId,
            CreatedUtc: createdUtcText,
            DeliverAfterUtc: null,
            EphemeralPublicKey: Base64Url.EncodeToString(ephemeral.ExportSubjectPublicKeyInfo()),
            HkdfSalt: Base64Url.EncodeToString(salt),
            Nonce: Base64Url.EncodeToString(nonce),
            Ciphertext: Base64Url.EncodeToString(ciphertextAndTag));
    }

    private static RemoteEnvelope ToStoredEnvelope(EncryptedEnvelope wire, DateTimeOffset receivedUtc) =>
        new(
            wire.MessageId,
            Base64Url.DecodeFromChars(wire.Ciphertext),
            Base64Url.DecodeFromChars(wire.EphemeralPublicKey),
            Base64Url.DecodeFromChars(wire.Nonce),
            null,
            wire.DeliverAfterUtc,
            receivedUtc)
        {
            HkdfSalt = Base64Url.DecodeFromChars(wire.HkdfSalt),
            CreatedUtc = wire.CreatedUtc,
        };
}
