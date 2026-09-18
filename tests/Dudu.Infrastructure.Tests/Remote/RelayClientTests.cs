using System.Net;
using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class RelayClientTests
{
    private static readonly Uri BaseUrl = new("https://relay.example.test/");

    [Fact]
    public void Public_http_base_url_is_rejected_before_authenticated_requests_are_possible()
    {
        Assert.Throws<ArgumentException>(() => new RelayClient(
            new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new InMemorySecretStore(),
            new RelayOptions(new Uri("http://relay.example.test/"))));
    }

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
    public async Task Requests_preserve_a_configured_relay_path_prefix_and_query()
    {
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent(
                    "{\"deviceId\":\"device-1\",\"desktopToken\":\"token-1\"," +
                    "\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00.000Z\"}"),
            };
        });
        var client = new RelayClient(
            new HttpClient(handler),
            new InMemorySecretStore(),
            new RelayOptions(new Uri("https://relay.example.test/some/path?tenant=dudu")));

        await client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://relay.example.test/some/path/v1/devices/register?tenant=dudu",
            requestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Register_writes_device_id_last_so_partial_storage_does_not_look_registered()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(
                "{\"deviceId\":\"device-1\",\"desktopToken\":\"token-1\",\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00.000Z\"}"),
        });
        var secretStore = new FailingSecretStore("relay-device-id-v1");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<IOException>(
            () => client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.True(secretStore.Values.ContainsKey("relay-desktop-token-v1"));
        Assert.False(secretStore.Values.ContainsKey("relay-device-id-v1"));
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

    [Theory]
    [InlineData(null, "token-2", "ABC123", "2026-01-01T00:00:00Z")]
    [InlineData("device-2", "", "ABC123", "2026-01-01T00:00:00Z")]
    [InlineData("device-2", "token-2", " ", "2026-01-01T00:00:00Z")]
    [InlineData("device-2", "token-2", "ABC123", "not-a-date")]
    public async Task Incomplete_registration_response_preserves_existing_secrets(
        string? deviceId, string token, string code, string expiry)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            deviceId,
            desktopToken = token,
            pairingCode = code,
            pairingCodeExpiresUtc = expiry,
        });
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(json),
        });
        var store = new InMemorySecretStore();
        store.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        store.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        store.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes("staged-token");
        var client = new RelayClient(new HttpClient(handler), store, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayProtocolException>(() =>
            client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.Equal("device-1", Encoding.UTF8.GetString(store.Values[RelaySecretKeys.DeviceId]));
        Assert.Equal("token-1", Encoding.UTF8.GetString(store.Values[RelaySecretKeys.DesktopToken]));
        Assert.Equal("staged-token", Encoding.UTF8.GetString(store.Values[RelaySecretKeys.DesktopTokenStaging]));
    }

    [Fact]
    public async Task Registration_marker_is_removed_before_replacing_an_existing_token()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(
                "{\"deviceId\":\"device-2\",\"desktopToken\":\"token-2\",\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00Z\"}"),
        });
        var store = new FailingSecretStore(RelaySecretKeys.DeviceId);
        store.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        store.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        var client = new RelayClient(new HttpClient(handler), store, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<IOException>(() =>
            client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.False(store.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.Equal("token-2", Encoding.UTF8.GetString(store.Values[RelaySecretKeys.DesktopToken]));
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

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Empty_pairing_code_response_throws_RelayProtocolException(string code)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            code,
            expiresUtc = "2026-01-01T00:00:00Z",
        });
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(json),
        });
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => client.CreatePairingCodeAsync(TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task GetDevice_not_found_throws_unauthorized_so_callers_converge_on_needs_repair()
    {
        // A 404 on the current-device query means the device row is gone server-side: the stored
        // credential is dead, so this must look like a 401 (NeedsRepair), not a generic protocol
        // error.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("stale-token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => client.GetDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acknowledge_not_found_is_idempotent_success()
    {
        // The relay's ack contract is idempotent: a 404 means the message already vanished
        // (expired between poll and ack, or acked by a concurrent poll), which is success.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            "relay-desktop-token-v1", Encoding.UTF8.GetBytes("token"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await client.AcknowledgeAsync("11111111-1111-1111-1111-111111111111", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RotateKey_stages_before_activating_and_clears_staging()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"desktopToken\":\"token-2\"}"),
        });
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            RelaySecretKeys.DesktopToken, Encoding.UTF8.GetBytes("token-1"), TestContext.Current.CancellationToken);
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        var returned = await client.RotateKeyAsync("public-key-spki", TestContext.Current.CancellationToken);

        Assert.Equal("token-2", returned);
        Assert.Equal(
            "token-2",
            Encoding.UTF8.GetString((await secretStore.GetAsync(
                RelaySecretKeys.DesktopToken, TestContext.Current.CancellationToken))!));
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
    }

    [Fact]
    public async Task RotateKey_staging_write_failure_forces_re_registration()
    {
        // The relay already rotated server-side, so the previous active token is dead. If the new
        // token could not even be staged it is lost: the registration marker must go, forcing a
        // fresh re-registration instead of polling forever with a dead credential.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"desktopToken\":\"token-2\"}"),
        });
        var secretStore = new FailingSecretStore(RelaySecretKeys.DesktopTokenStaging);
        secretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<IOException>(
            () => client.RotateKeyAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
    }

    [Fact]
    public async Task RotateKey_activation_failure_keeps_the_staged_token_and_the_pairing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"desktopToken\":\"token-2\"}"),
        });
        var secretStore = new FailingSecretStore(RelaySecretKeys.DesktopToken);
        secretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<IOException>(
            () => client.RotateKeyAsync("public-key-spki", TestContext.Current.CancellationToken));

        Assert.True(secretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.Equal("token-2", Encoding.UTF8.GetString(secretStore.Values[RelaySecretKeys.DesktopTokenStaging]));
    }

    [Fact]
    public async Task Unauthorized_request_promotes_a_staged_token_and_retries_once()
    {
        var authorizations = new List<string?>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var token = request.Headers.Authorization?.Parameter;
            authorizations.Add(token);
            return token == "token-2"
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var secretStore = new InMemorySecretStore();
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        secretStore.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes("token-2");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await client.AcknowledgeAsync("11111111-1111-1111-1111-111111111111", TestContext.Current.CancellationToken);

        Assert.Equal(["token-1", "token-2"], authorizations);
        Assert.Equal("token-2", Encoding.UTF8.GetString(secretStore.Values[RelaySecretKeys.DesktopToken]));
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
    }

    [Fact]
    public async Task Unauthorized_request_with_a_dead_staged_token_retries_only_once()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var secretStore = new InMemorySecretStore();
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        secretStore.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes("token-2");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => client.GetDeviceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.RequestCount);
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
    }

    [Fact]
    public async Task Register_discards_a_stale_staged_token_from_the_previous_device()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(
                "{\"deviceId\":\"device-2\",\"desktopToken\":\"token-new\",\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00.000Z\"}"),
        });
        var secretStore = new InMemorySecretStore();
        secretStore.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes("old-device-token");
        var client = new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));

        await client.RegisterAsync("public-key-spki", TestContext.Current.CancellationToken);

        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    // P1: the staging-cleanup failure in RotateKeyAsync must leave a dedicated diagnostic
    // (event 1008) that carries only the exception type — both token values are genuinely in
    // scope here and must never reach the logs.
    [Fact]
    public async Task RotateKey_staging_cleanup_failure_is_logged_without_tokens()
    {
        const string activeToken = "staging-cleanup-active-9182";
        const string rotatedToken = "staging-cleanup-rotated-9183";
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"desktopToken\":\"" + rotatedToken + "\"}"),
        });
        var secretStore = new DeleteFailingSecretStore(RelaySecretKeys.DesktopTokenStaging);
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes(activeToken);
        var sink = new RecordingLoggerSink();
        var client = new RelayClient(
            new HttpClient(handler), secretStore, new RelayOptions(BaseUrl), new RecordingLogger<RelayClient>(sink));

        var returned = await client.RotateKeyAsync("public-key-spki", TestContext.Current.CancellationToken);

        Assert.Equal(rotatedToken, returned);
        Assert.Contains(1008, sink.EventIds);
        Assert.Contains("IOException", sink.JoinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(activeToken, sink.JoinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedToken, sink.JoinedText, StringComparison.Ordinal);
    }

    // P1: the 401 staged-token promotion must leave a dedicated diagnostic (event 1009) that
    // carries only the status and a fixed label — the promoted token value itself is never logged.
    [Fact]
    public async Task Staged_token_promotion_is_logged_without_tokens()
    {
        const string activeToken = "staged-promotion-active-7741";
        const string stagedToken = "staged-promotion-staged-7742";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var token = request.Headers.Authorization?.Parameter;
            return token == stagedToken
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var secretStore = new InMemorySecretStore();
        secretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes(activeToken);
        secretStore.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes(stagedToken);
        var sink = new RecordingLoggerSink();
        var client = new RelayClient(
            new HttpClient(handler), secretStore, new RelayOptions(BaseUrl), new RecordingLogger<RelayClient>(sink));

        await client.AcknowledgeAsync("11111111-1111-1111-1111-111111111111", TestContext.Current.CancellationToken);

        Assert.Contains(1009, sink.EventIds);
        Assert.Contains("promoted-staged-token", sink.JoinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(activeToken, sink.JoinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(stagedToken, sink.JoinedText, StringComparison.Ordinal);
        Assert.False(secretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
    }

    private sealed class FailingSecretStore(string failingKey) : ISecretStore
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            if (key == failingKey)
            {
                throw new IOException("simulated secret-store failure");
            }

            Values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    // P1: fails only the staging-slot delete, so RotateKeyAsync reaches its non-fatal
    // staging-cleanup catch with real token values in scope.
    private sealed class DeleteFailingSecretStore(string failingKey) : ISecretStore
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            Values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key == failingKey)
            {
                throw new IOException("simulated secret-store failure");
            }

            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    // P1: captures every string the client hands to a logger, mirroring the sink in
    // PrivacyBoundaryTests, so the new staging diagnostics can assert both presence and secrecy.
    private sealed class RecordingLoggerSink
    {
        private readonly List<string> _entries = [];
        private readonly List<int> _eventIds = [];
        private readonly object _sync = new();

        public void Add(string entry)
        {
            lock (_sync)
            {
                _entries.Add(entry);
            }
        }

        public void AddEventId(int eventId)
        {
            lock (_sync)
            {
                _eventIds.Add(eventId);
            }
        }

        public string JoinedText
        {
            get
            {
                lock (_sync)
                {
                    return string.Join('\n', _entries);
                }
            }
        }

        public IReadOnlyList<int> EventIds
        {
            get
            {
                lock (_sync)
                {
                    return [.. _eventIds];
                }
            }
        }
    }

    private sealed class RecordingLogger<T>(RecordingLoggerSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Add(formatter(state, exception));
            sink.AddEventId(eventId.Id);
            if (exception is not null)
            {
                sink.Add(exception.GetType().FullName ?? string.Empty);
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var pair in structuredState)
                {
                    sink.Add(pair.Key + "=" + pair.Value);
                }
            }
        }
    }

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
