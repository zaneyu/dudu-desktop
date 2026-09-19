using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class OfflinePairingServiceTests
{
    [Fact]
    public async Task ForgetPairing_succeeds_with_nothing_to_forget()
    {
        // M4: the default IPairingService.ForgetPairingAsync throws NotSupportedException with a
        // developer-facing message the Connection page would show verbatim. The common case for
        // this offline implementation is that no relay was ever configured, so there is nothing
        // stored at all -- that must still succeed cleanly, not throw.
        var secretStore = new InMemorySecretStore();
        var service = new OfflinePairingService(secretStore);

        await service.ForgetPairingAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForgetPairing_best_effort_clears_stray_secrets_left_by_a_previously_configured_relay()
    {
        // M4: a relay could have been configured once (leaving registration secrets and the
        // desktop key behind) and later removed from settings, so this build now runs the offline
        // implementation. Those stray secrets should still be cleaned up when forget is called.
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(RelaySecretKeys.DeviceId, "device-1"u8.ToArray(), TestContext.Current.CancellationToken);
        await secretStore.SetAsync(RelaySecretKeys.DesktopToken, "token-1"u8.ToArray(), TestContext.Current.CancellationToken);
        await secretStore.SetAsync(RelaySecretKeys.DesktopTokenStaging, "token-2"u8.ToArray(), TestContext.Current.CancellationToken);
        await secretStore.SetAsync(DesktopKeyService.SecretStoreKey, new byte[] { 0x01, 0x02 }, TestContext.Current.CancellationToken);
        var service = new OfflinePairingService(secretStore);

        await service.ForgetPairingAsync(TestContext.Current.CancellationToken);

        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
        Assert.False(secretStore.Values.ContainsKey(DesktopKeyService.SecretStoreKey));
    }
}
