using System.Net;
using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class RelayClientTests
{
    private static readonly Uri BaseUrl = new("https://relay.example.test/");

    [Fact]
    public async Task Register_stores_device_id_and_token_only_after_a_valid_response()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(
                "{\"deviceId\":\"device-1\",\"desktopToken\":\"token-1\"," +
                "\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00.000Z\"}"),
        });
        var secretStore = new InMemorySecretStore();
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        var result = await client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken);

        Assert.Equal("device-1", result.DeviceId);
        Assert.Equal("token-1", result.DesktopToken);
        Assert.Equal(
            "device-1",
            Encoding.UTF8.GetString((await secretStore.GetAsync("relay-device-id-v1", TestContext.Current.CancellationToken))!));
        Assert.Equal(
            "token-1",
            Encoding.UTF8.GetString((await secretStore.GetAsync("relay-desktop-token-v1", TestContext.Current.CancellationToken))!));
    }

    [Fact]
    public async Task Register_with_a_malformed_response_stores_nothing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent("{\"deviceId\":\"device-1\""), // truncated / malformed JSON
        });
        var secretStore = new InMemorySecretStore();
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.Empty(secretStore.Values);
    }

    [Fact]
    public async Task Unauthorized_response_throws_RelayUnauthorizedException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("stale-token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => client.GetDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Server_error_response_throws_RelayUnavailableException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayUnavailableException>(
            () => client.GetDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acknowledge_sends_no_body_and_does_not_throw_on_success()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await client.AcknowledgeAsync("11111111-1111-1111-1111-111111111111", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    /// <summary>A fake transport that records how many requests it served and answers with a
    /// caller-supplied response, never touching the network.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_respond(request));
        }
    }
}
