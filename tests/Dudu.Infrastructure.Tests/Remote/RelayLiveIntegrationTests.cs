using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

/// <summary>
/// Exercises <see cref="RelayClient"/> against a real relay Worker started locally via
/// <c>npx wrangler dev --local --port 8787</c>. Skipped unless <c>DUDU_RELAY_BASE_URL</c> is set,
/// so it never runs (and never fails) as part of the ordinary offline test suite.
/// </summary>
public sealed class RelayLiveIntegrationTests
{
    [Fact]
    public async Task Register_create_pairing_code_and_poll_against_a_running_worker()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DUDU_RELAY_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Assert.Skip("DUDU_RELAY_BASE_URL is not set; start `npx wrangler dev --local --port 8787` and set it to run this test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var secretStore = new InMemorySecretStore();
        var client = new RelayClient(httpClient, secretStore, new RelayOptions(new Uri(baseUrl)));
        var keyService = new DesktopKeyService(secretStore);

        var keyMaterial = await keyService.GetOrCreateAsync(cancellationToken);
        var registration = await client.RegisterAsync(keyMaterial.PublicKeySpkiBase64Url, cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(registration.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(registration.DesktopToken));

        var device = await client.GetDeviceAsync(cancellationToken);
        Assert.Equal(0, device.ActiveSenderSessions);

        var pairingCode = await client.CreatePairingCodeAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(pairingCode.Code));
        Assert.True(pairingCode.ExpiresUtc > DateTimeOffset.UtcNow);

        // A brand new device has nothing queued yet; polling must succeed (not 401/5xx) and
        // simply return no envelopes.
        var envelopes = await client.PollAsync(cancellationToken);
        Assert.Empty(envelopes);

        await client.DeleteDeviceAsync(cancellationToken);
    }
}
