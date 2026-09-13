using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dudu.Infrastructure.Crypto;
using Xunit;

namespace Dudu.Infrastructure.Tests.Crypto;

public sealed class EnvelopeCryptoTests
{
    [Fact]
    public void Decrypt_rejects_a_modified_authentication_tag()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { Ciphertext = FlipLastBit(envelope.Ciphertext) };

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_modified_message_id_because_it_changes_the_authenticated_data()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { MessageId = Guid.NewGuid().ToString() };

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_modified_nonce()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { Nonce = FlipLastBit(envelope.Nonce) };

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_non_v1_protocol_version()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { ProtocolVersion = 2 };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_invalid_message_id_format()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { MessageId = "not-a-uuid" };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_created_timestamp_more_than_five_minutes_in_the_future()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(
            recipient.ExportSubjectPublicKeyInfo(),
            "private hello",
            createdUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_hkdf_salt_of_the_wrong_length()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { HkdfSalt = Base64Url.EncodeToString(new byte[16]) };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_nonce_of_the_wrong_length()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { Nonce = Base64Url.EncodeToString(new byte[8]) };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_undersized_ciphertext()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { Ciphertext = Base64Url.EncodeToString(new byte[10]) };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_oversized_ciphertext()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { Ciphertext = Base64Url.EncodeToString(new byte[6145]) };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_ephemeral_key_that_is_not_a_valid_p256_spki_key()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
        envelope = envelope with { EphemeralPublicKey = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(91)) };

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_a_decrypted_payload_over_4096_utf8_bytes()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        // 1,100 four-byte UTF-8 scalar values (1,100 < 2,000) push the JSON payload itself
        // over the 4,096 byte ceiling without tripping the separate text-length rule.
        var text = string.Concat(Enumerable.Repeat("\U0001F642", 1100));
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), text);

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_text_over_2000_unicode_scalar_values()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var text = new string('a', 2001);
        var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), text);

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_an_unknown_reaction()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var envelope = CryptoFixture.EncryptFor(
            recipient.ExportSubjectPublicKeyInfo(),
            "private hello",
            reaction: "party");

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void Decrypt_rejects_unknown_payload_keys()
    {
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var rawPayload = Encoding.UTF8.GetBytes(
            """{"kind":"note","text":"hi","reaction":"none","extra":"nope"}""");
        var envelope = CryptoFixture.EncryptRawPayloadFor(recipient.ExportSubjectPublicKeyInfo(), rawPayload);

        Assert.Throws<EnvelopeValidationException>(() =>
            EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public async Task Desktop_key_is_reused_from_Dpapi_secret_store()
    {
        var store = new InMemorySecretStore();
        var service = new DesktopKeyService(store);

        var first = await service.GetOrCreateAsync(TestContext.Current.CancellationToken);
        var second = await service.GetOrCreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first.PublicKeySpkiBase64Url, second.PublicKeySpkiBase64Url);
        Assert.Single(store.Values);
    }

    [Fact]
    public async Task Decrypt_interoperates_with_an_envelope_produced_by_the_node_fixture()
    {
        var nodePath = FindNodeOnPath();
        if (nodePath is null)
        {
            Assert.Skip("node executable not found on PATH.");
            return;
        }

        var fixturePath = Path.Combine(FindRepoRoot(), "relay", "dist", "src", "protocol", "encrypt-fixture.js");
        if (!File.Exists(fixturePath))
        {
            Assert.Skip($"Node fixture not built. Run 'npm run build' in relay/ (expected {fixturePath}).");
            return;
        }

        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBase64Url = Base64Url.EncodeToString(recipient.ExportSubjectPublicKeyInfo());

        var startInfo = new ProcessStartInfo(nodePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("--public");
        startInfo.ArgumentList.Add(publicKeyBase64Url);
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add("hello from node");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the node process.");
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, $"node fixture failed (exit {process.ExitCode}): {stderr}");

        var envelope = JsonSerializer.Deserialize(stdout, EnvelopeJsonContext.Default.EncryptedEnvelope)
            ?? throw new InvalidOperationException("Failed to parse the envelope JSON produced by the node fixture.");

        var payload = EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey());

        Assert.Equal("note", payload.Kind);
        Assert.Equal("hello from node", payload.Text);
        Assert.Equal("none", payload.Reaction);
    }

    private static string FlipLastBit(string base64UrlValue)
    {
        var bytes = Base64Url.DecodeFromChars(base64UrlValue);
        bytes[^1] ^= 0x01;
        return Base64Url.EncodeToString(bytes);
    }

    private static string? FindNodeOnPath()
    {
        var pathEnvironmentVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var executableName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in pathEnvironmentVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuduDesktop.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (DuduDesktop.slnx) above {AppContext.BaseDirectory}.");
    }
}
