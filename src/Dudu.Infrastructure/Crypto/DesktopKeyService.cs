using System.Buffers.Text;
using System.Security.Cryptography;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Security;

namespace Dudu.Infrastructure.Crypto;

/// <summary>
/// The desktop's half of the version-one key pair: a P-256 ECDH key whose private bytes are
/// generated once and then reused forever, stored only via <see cref="ISecretStore"/> (DPAPI
/// on Windows) and never written to disk in any other form.
/// </summary>
/// <param name="PublicKeySpkiBase64Url">
/// The desktop's public key, SPKI DER encoded and then Base64URL encoded, suitable for
/// publishing to a sender so it can address envelopes to this desktop.
/// </param>
/// <param name="PrivateKeyPkcs8">
/// The desktop's private key, PKCS#8 DER encoded. Treat as key material: never log it.
/// </param>
public sealed record DesktopKeyMaterial(string PublicKeySpkiBase64Url, byte[] PrivateKeyPkcs8);

/// <summary>
/// Creates the desktop's ECDH key pair on first use and reuses it afterwards by round-tripping
/// the PKCS#8 private key through <see cref="ISecretStore"/>.
/// </summary>
public sealed class DesktopKeyService
{
    internal const string SecretStoreKey = "desktop-ecdh-private-v1";

    private readonly ISecretStore _secretStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopKeyService(ISecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public async Task<DesktopKeyMaterial> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existingPrivateKey = await _secretStore.GetAsync(SecretStoreKey, cancellationToken);
            if (existingPrivateKey is not null)
            {
                using var existing = ECDiffieHellman.Create();
                try
                {
                    existing.ImportPkcs8PrivateKey(existingPrivateKey, out _);
                }
                catch (CryptographicException exception)
                {
                    // H2/M5: a stored key that decrypts (DPAPI succeeded) but does not parse as
                    // PKCS#8 is just as dead an end as an unreadable secret -- it will never parse
                    // differently on retry. Wrap it the same way so every caller converges on
                    // NeedsRepair instead of an unhandled CryptographicException.
                    throw new SecretStoreException(
                        "The stored desktop private key could not be parsed.", exception);
                }

                return new DesktopKeyMaterial(
                    Base64Url.EncodeToString(existing.ExportSubjectPublicKeyInfo()),
                    existingPrivateKey);
            }

            using var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var privateKeyPkcs8 = created.ExportPkcs8PrivateKey();
            await _secretStore.SetAsync(SecretStoreKey, privateKeyPkcs8, cancellationToken);

            return new DesktopKeyMaterial(
                Base64Url.EncodeToString(created.ExportSubjectPublicKeyInfo()),
                privateKeyPkcs8);
        }
        finally
        {
            _gate.Release();
        }
    }
}
