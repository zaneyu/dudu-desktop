using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Logging;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dudu.Infrastructure.Tests.Security;

/// <summary>
/// Task 21: proves the privacy and outage boundaries the relay/remote-sync path promises —
/// nothing secret ever reaches a logger, a misbehaving relay cannot force unbounded memory use,
/// and a relay outage never stops locally-scheduled reminders.
/// </summary>
public sealed class PrivacyBoundaryTests
{
    [Fact]
    public async Task Logs_never_include_note_text_tokens_or_private_key_material()
    {
        var fixture = PrivacyFixture.WithSecrets(
            note: "uniquely-private-phrase-7491",
            token: "desktop-token-8821",
            privateKeyMarker: "private-key-marker-3307");

        await fixture.ExerciseRegistrationPollFailureAndRevealAsync();

        var logs = fixture.LogSink.JoinedText;
        Assert.DoesNotContain("uniquely-private-phrase-7491", logs);
        Assert.DoesNotContain("desktop-token-8821", logs);
        Assert.DoesNotContain("private-key-marker-3307", logs);
    }

    [Fact]
    public async Task Relay_client_rejects_response_larger_than_sixty_four_kib()
    {
        var client = RelayClientFixture.RespondingWithBytes(65 * 1024);

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => client.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Worker_outage_does_not_stop_local_reminders()
    {
        var fixture = AppFixture.WithUnavailableRelayAndDueReminder();

        await fixture.Host.StartAsync(fixture.CancellationToken);

        Assert.True(fixture.Host.IsStarted); // AppHost has no `IsRunning`; see task-21 ruling 3.
        Assert.Single(fixture.PresentedReminders);
    }

    /// <summary>Drives a real <see cref="RelayClient"/> through registration, a poll failure,
    /// and an envelope reveal/key-corruption path, capturing every string any attached logger
    /// ever receives.</summary>
    private sealed class PrivacyFixture
    {
        private static readonly Uri BaseUrl = new("https://relay.example.test/");

        private readonly string _note;
        private readonly string _token;
        private readonly string _privateKeyMarker;

        private PrivacyFixture(string note, string token, string privateKeyMarker)
        {
            _note = note;
            _token = token;
            _privateKeyMarker = privateKeyMarker;
        }

        public static PrivacyFixture WithSecrets(string note, string token, string privateKeyMarker) =>
            new(note, token, privateKeyMarker);

        public RecordingLoggerSink LogSink { get; } = new();

        public async Task ExerciseRegistrationPollFailureAndRevealAsync()
        {
            var relayLogger = new RecordingLogger<RelayClient>(LogSink);
            var secretStore = new InMemorySecretStore();

            // 1. Registration: the relay echoes the token marker back. RelayClient must store it
            //    via the secret store and never hand it to a logger.
            var registerHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent(
                    "{\"deviceId\":\"device-1\",\"desktopToken\":\"" + _token + "\"," +
                    "\"pairingCode\":\"ABC123\",\"pairingCodeExpiresUtc\":\"2026-01-01T00:00:00.000Z\"}"),
            });
            var registeringClient = new RelayClient(
                new HttpClient(registerHandler), secretStore, new RelayOptions(BaseUrl), relayLogger);
            await registeringClient.RegisterAsync("public-key-spki-placeholder", CancellationToken.None);

            // 2. Poll failure: the relay is unavailable. PrivacySafeLog.RelayFailed may log a
            //    status code and a category, but nothing from the request or response body.
            var failingClient = new RelayClient(
                new HttpClient(new FakeHttpMessageHandler(
                    _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
                secretStore,
                new RelayOptions(BaseUrl),
                relayLogger);
            await Assert.ThrowsAsync<RelayUnavailableException>(
                () => failingClient.PollAsync(CancellationToken.None));

            // 3. Reveal: decrypt a real envelope carrying the note text end-to-end.
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var envelope = CryptoFixture.EncryptFor(ecdh.ExportSubjectPublicKeyInfo(), _note);
            var decrypted = EnvelopeCrypto.Decrypt(envelope, ecdh.ExportPkcs8PrivateKey(), DateTimeOffset.UtcNow);
            Assert.Equal(_note, decrypted.Text);

            // 4. Corrupted private-key material: stand in for leaked/garbled desktop key bytes
            //    and exercise the real key-loading path, which must fail without ever surfacing
            //    the marker anywhere a logger could see it.
            var keySecretStore = new InMemorySecretStore();
            keySecretStore.Values[DesktopKeyService.SecretStoreKey] = Encoding.UTF8.GetBytes(_privateKeyMarker);
            var keyService = new DesktopKeyService(keySecretStore);
            await Assert.ThrowsAsync<CryptographicException>(
                () => keyService.GetOrCreateAsync(CancellationToken.None));
        }

        private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");
    }

    /// <summary>Builds a <see cref="RelayClient"/> whose transport answers every request with a
    /// response body of a caller-chosen byte length, to exercise the bounded-read boundary
    /// without needing a real relay.</summary>
    private static class RelayClientFixture
    {
        private static readonly Uri BaseUrl = new("https://relay.example.test/");

        public static RelayClient RespondingWithBytes(int byteCount)
        {
            var oversizedBody = new string('a', byteCount);
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversizedBody, Encoding.UTF8, "application/json"),
            });
            var secretStore = new InMemorySecretStore();
            secretStore.Values["relay-desktop-token-v1"] = Encoding.UTF8.GetBytes("token");
            return new RelayClient(new HttpClient(handler), secretStore, new RelayOptions(BaseUrl));
        }
    }

    /// <summary>A fake transport that answers with a caller-supplied response, never touching
    /// the network. Mirrors the pattern in <c>RelayClientTests</c>.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    /// <summary>Collects every string any <see cref="RecordingLogger{T}"/> formats, across
    /// however many logger instances share it.</summary>
    private sealed class RecordingLoggerSink
    {
        private readonly List<string> _entries = [];
        private readonly object _sync = new();

        public void Add(string entry)
        {
            lock (_sync)
            {
                _entries.Add(entry);
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
    }

    /// <summary>An <see cref="ILogger{TCategoryName}"/> that records the formatted message and
    /// every structured state value it is given, rather than writing anywhere, so a test can
    /// assert on exactly what text a logger was ever handed.</summary>
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
            if (exception is not null)
            {
                sink.Add(exception.GetType().FullName ?? string.Empty);
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var pair in structuredState)
                {
                    sink.Add($"{pair.Key}={pair.Value}");
                }
            }
        }
    }

    /// <summary>A minimal <see cref="AppHost"/> rig: a reminder service that records every tick
    /// it is asked to present, and a remote sync that always throws, simulating a Worker
    /// outage. No real database, timer, or relay is involved.</summary>
    private sealed class AppFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));

        private AppFixture()
        {
            Directory.CreateDirectory(_root);
            var reminderService = new RecordingReminderService();
            PresentedReminders = reminderService.Ticks;
            Host = new AppHost(
                AppPaths.ForRoot(_root),
                new NoOpDatabase(),
                reminderService);
            Host.AttachRemoteSync(new UnavailableRemoteSync());
        }

        public static AppFixture WithUnavailableRelayAndDueReminder() => new();

        public AppHost Host { get; }

        public IReadOnlyList<int> PresentedReminders { get; }

        public CancellationToken CancellationToken => CancellationToken.None;

        public void Dispose()
        {
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private sealed class NoOpDatabase : IAppHostDatabase
        {
            public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Stands in for a due local reminder being presented: every tick is recorded,
        /// with no real <c>ReminderEngine</c> involved.</summary>
        private sealed class RecordingReminderService : IAppHostReminderService
        {
            public List<int> Ticks { get; } = [];

            public Task TickAsync(CancellationToken cancellationToken = default)
            {
                Ticks.Add(Ticks.Count + 1);
                return Task.CompletedTask;
            }
        }

        private sealed class UnavailableRemoteSync : IAppHostRemoteSync
        {
            public Task StartAsync(CancellationToken cancellationToken = default) =>
                throw new RelayUnavailableException("The relay could not be reached.");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
