using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Security;
using Xunit;

namespace Dudu.Infrastructure.Tests.Crypto;

public sealed class DesktopKeyServiceTests
{
    [Fact]
    public async Task GetOrCreate_wraps_an_unparsable_stored_key_as_a_secret_store_exception()
    {
        // H2/M5: a key that decrypts (DPAPI succeeded) but does not parse as PKCS#8 is exactly as
        // dead an end as a key DPAPI cannot decrypt at all -- neither becomes readable on retry.
        // RemoteSyncService's callers only know how to converge unreadable-secret failures on
        // NeedsRepair via SecretStoreException, so a raw CryptographicException here would
        // otherwise surface as an unhandled error instead.
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            DesktopKeyService.SecretStoreKey,
            new byte[] { 0x00, 0x01, 0x02, 0x03 },
            TestContext.Current.CancellationToken);
        var service = new DesktopKeyService(secretStore);

        await Assert.ThrowsAsync<SecretStoreException>(
            () => service.GetOrCreateAsync(TestContext.Current.CancellationToken));
    }
}
