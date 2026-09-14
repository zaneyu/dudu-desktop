using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
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
            token: "desktop-token-8821");

        await fixture.ExerciseRegistrationPollFailureAndRevealAsync();

        var logs = fixture.LogSink.JoinedText;
        Assert.DoesNotContain("uniquely-private-phrase-7491", logs);
        Assert.DoesNotContain("desktop-token-8821", logs);
    }

    [Fact]
    public void DesktopKeyService_never_accepts_a_logger_dependency()
    {
        // Cheap structural guard, independent of the marker-based assertion above: the desktop's
        // private key material only ever flows through DesktopKeyService (see GetOrCreateAsync).
        // Asserting that none of its public constructors take an ILogger/ILogger<T> parameter at
        // all proves key material can never reach a logger through this class, regardless of
        // what any caller does or what any future change to this class's body might introduce.
        foreach (var constructor in typeof(DesktopKeyService).GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                var parameterType = parameter.ParameterType;
                var isLogger = typeof(ILogger).IsAssignableFrom(parameterType)
                    || (parameterType.IsGenericType
                        && parameterType.GetGenericTypeDefinition() == typeof(ILogger<>));
                Assert.False(
                    isLogger,
                    $"{typeof(DesktopKeyService)} must never take an ILogger dependency.");
            }
        }
    }

    [Fact]
    public async Task Relay_client_rejects_response_larger_than_sixty_four_kib()
    {
        var client = RelayClientFixture.RespondingWithBytes(65 * 1024);

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => client.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oversized_relay_page_reports_a_terminal_state_and_leaves_the_loop_alive()
    {
        // Review C1: the relay's own page cap was 20 rows with no byte budget, so a page of 20
        // maximum-size envelopes (6144 bytes of ciphertext each) sails past the desktop's 64 KiB
        // BoundedJsonContent limit. RelayClient then throws RelayProtocolException on that page
        // every single time, byte for byte. Before the fix RunLoopAsync caught it as an ordinary
        // RemoteSyncException and retried on the short ramp forever: the desktop polled, failed,
        // and re-polled with nothing ever shown to the user -- a permanent silent stall.
        var secretStore = new InMemorySecretStore();
        secretStore.Values["relay-desktop-token-v1"] = Encoding.UTF8.GetBytes("token");
        secretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-already-registered");
        var relay = RelayClientFixture.RespondingWithEnvelopePage(
            secretStore, envelopeCount: 20, ciphertextBytes: 6144);

        var reported = new List<string>();
        await using var sync = new RemoteSyncService(
            relay,
            new AcceptingEnvelopeRepository(),
            new NeverReachedArrivalSink(),
            secretStore,
            new DesktopKeyService(secretStore),
            new RealClock(),
            new PollBackoff(new ZeroRandomSource()),
            reportError: (tag, _) => reported.Add(tag));

        await sync.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (sync.StatusReason != PairingStatusReason.RelayProtocolError
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            Assert.Equal(PairingStatusReason.RelayProtocolError, sync.StatusReason);
            Assert.Contains("remote-sync-protocol", reported);

            // Alive, not dead and not spinning: it is sitting in the capped backoff, so a relay
            // that is fixed later still recovers without an app restart.
            Assert.True(sync.IsRunning, "the poll loop must survive an unreadable relay page");
        }
        finally
        {
            await sync.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Worker_outage_does_not_stop_local_reminders()
    {
        var fixture = AppFixture.WithUnavailableRelayAndDueReminder();

        await fixture.Host.StartAsync(fixture.CancellationToken);

        Assert.True(fixture.Host.IsStarted); // AppHost has no `IsRunning`; see task-21 ruling 1.
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

        private PrivacyFixture(string note, string token)
        {
            _note = note;
            _token = token;
        }

        public static PrivacyFixture WithSecrets(string note, string token) =>
            new(note, token);

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

            // 4. Decrypt-failed envelope with real key material genuinely in scope: a real ECDH
            //    key is loaded successfully (so GetOrCreateAsync does not throw, unlike round 1's
            //    fix), and the relay returns one well-formed envelope whose ciphertext is
            //    tampered so EnvelopeCrypto.Decrypt throws inside ProcessEnvelopeAsync's own
            //    catch. That catch is the one call site in RemoteSyncService that logs
            //    (PrivacySafeLog.EnvelopeRejected, EventId 1003) on a path where the private key
            //    was just used, so asserting the marker is absent here is actually falsifiable —
            //    unlike round 1's key-load failure, which threw before any PrivacySafeLog call
            //    ever ran and so could never have caught a leak.
            var keySecretStore = new InMemorySecretStore();
            // Both registration markers are required; seeding only the id would correctly cause
            // RemoteSyncService to repair a partial registration before polling.
            keySecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-already-registered");
            keySecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-already-registered");
            var keyService = new DesktopKeyService(keySecretStore);
            var keyMaterial = await keyService.GetOrCreateAsync(CancellationToken.None);
            var storedPrivateKeyBytes = keySecretStore.Values[DesktopKeyService.SecretStoreKey];
            var privateKeyMarkerBase64 = Convert.ToBase64String(storedPrivateKeyBytes);
            var privateKeyMarkerBase64Url = Base64Url.EncodeToString(storedPrivateKeyBytes);

            using var recipientPublicKey = ECDiffieHellman.Create();
            recipientPublicKey.ImportSubjectPublicKeyInfo(
                Base64Url.DecodeFromChars(keyMaterial.PublicKeySpkiBase64Url), out _);
            var wellFormedEnvelope = CryptoFixture.EncryptFor(
                recipientPublicKey.ExportSubjectPublicKeyInfo(), _note);
            var tamperedCiphertextAndTag = Base64Url.DecodeFromChars(wellFormedEnvelope.Ciphertext);
            tamperedCiphertextAndTag[0] ^= 0xFF;
            var tamperedEnvelope = new RelayEnvelope(
                wellFormedEnvelope.ProtocolVersion,
                wellFormedEnvelope.MessageId,
                wellFormedEnvelope.CreatedUtc,
                wellFormedEnvelope.DeliverAfterUtc,
                wellFormedEnvelope.EphemeralPublicKey,
                wellFormedEnvelope.HkdfSalt,
                wellFormedEnvelope.Nonce,
                Base64Url.EncodeToString(tamperedCiphertextAndTag));

            Exception? reportedException = null;
            var syncLogger = new RecordingLogger<RemoteSyncService>(LogSink);
            await using var sync = new RemoteSyncService(
                new SingleEnvelopeRelayClient(tamperedEnvelope),
                new AcceptingEnvelopeRepository(),
                new NeverReachedArrivalSink(),
                keySecretStore,
                keyService,
                new RealClock(),
                new PollBackoff(new ZeroRandomSource()),
                reportError: (_, exception) => reportedException = exception,
                logger: syncLogger);

            // Must not throw: the decrypt failure is caught inside ProcessEnvelopeAsync.
            await sync.PollOnceAsync(CancellationToken.None);

            // 5. Review C2: the same loop, fed a payload whose fields are JSON null. It is
            //    undecryptable in exactly the same way (and used to throw a
            //    NullReferenceException out of the loop), so it takes the same logging path --
            //    which must stay just as free of key, token, and note material.
            var nullFieldEnvelope = CryptoFixture.EncryptRawPayloadFor(
                recipientPublicKey.ExportSubjectPublicKeyInfo(),
                Encoding.UTF8.GetBytes("""{"kind":"note","text":null,"reaction":"none"}"""));
            await using var nullFieldSync = new RemoteSyncService(
                new SingleEnvelopeRelayClient(new RelayEnvelope(
                    nullFieldEnvelope.ProtocolVersion,
                    nullFieldEnvelope.MessageId,
                    nullFieldEnvelope.CreatedUtc,
                    nullFieldEnvelope.DeliverAfterUtc,
                    nullFieldEnvelope.EphemeralPublicKey,
                    nullFieldEnvelope.HkdfSalt,
                    nullFieldEnvelope.Nonce,
                    nullFieldEnvelope.Ciphertext)),
                new AcceptingEnvelopeRepository(),
                new NeverReachedArrivalSink(),
                keySecretStore,
                keyService,
                new RealClock(),
                new PollBackoff(new ZeroRandomSource()),
                reportError: (_, exception) => reportedException = exception,
                logger: syncLogger);

            // Must not throw: a null payload field is an ordinary validation failure now.
            await nullFieldSync.PollOnceAsync(CancellationToken.None);

            Assert.Contains(1003, LogSink.EventIds);
            Assert.DoesNotContain(privateKeyMarkerBase64, LogSink.JoinedText);
            Assert.DoesNotContain(privateKeyMarkerBase64Url, LogSink.JoinedText);
            Assert.DoesNotContain(_token, LogSink.JoinedText);
            Assert.DoesNotContain(_note, LogSink.JoinedText);

            Assert.NotNull(reportedException);
            Assert.DoesNotContain(privateKeyMarkerBase64, reportedException!.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(privateKeyMarkerBase64Url, reportedException.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_token, reportedException.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_note, reportedException.Message, StringComparison.Ordinal);
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

        /// <summary>Answers every request with a well-formed <c>GET /v1/messages</c> body of
        /// <paramref name="envelopeCount"/> envelopes carrying <paramref name="ciphertextBytes"/>
        /// bytes of ciphertext each -- the largest page the relay's row cap alone would allow.</summary>
        public static RelayClient RespondingWithEnvelopePage(
            InMemorySecretStore secretStore, int envelopeCount, int ciphertextBytes)
        {
            var ciphertext = Base64Url.EncodeToString(new byte[ciphertextBytes]);
            var envelopes = string.Join(",", Enumerable.Range(0, envelopeCount).Select(index =>
                $$"""
                {"protocolVersion":1,"messageId":"{{Guid.NewGuid():D}}",
                 "createdUtc":"2026-01-01T00:00:00.000Z","deliverAfterUtc":null,
                 "ephemeralPublicKey":"AA","hkdfSalt":"AA","nonce":"AA","ciphertext":"{{ciphertext}}"}
                """));
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"messages":[{{envelopes}}]}""", Encoding.UTF8, "application/json"),
            });
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
            sink.AddEventId(eventId.Id);
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

    /// <summary>An <see cref="IRelayClient"/> double for the decrypt-failed test: registration is
    /// skipped because both registration markers are seeded, so PollAsync returns the one
    /// caller-supplied (tampered) envelope and AcknowledgeAsync is a no-op — ProcessEnvelopeAsync
    /// acknowledges every envelope it processes, decrypted or not. Every other member must never
    /// run, so it throws.</summary>
    private sealed class SingleEnvelopeRelayClient(RelayEnvelope envelope) : IRelayClient
    {
        public Task<RelayRegistrationResult> RegisterAsync(string publicKeySpki, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RegisterAsync must not run: both registration markers are seeded.");

        public Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("GetDeviceAsync must not run in this test.");

        public Task<RelayPairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("CreatePairingCodeAsync must not run in this test.");

        public Task<string> RotateKeyAsync(string publicKeySpki, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RotateKeyAsync must not run in this test.");

        public Task DeleteDeviceAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("DeleteDeviceAsync must not run in this test.");

        public Task<IReadOnlyList<RelayEnvelope>> PollAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RelayEnvelope>>([envelope]);

        public Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>An <see cref="IRemoteEnvelopeRepository"/> double for the decrypt-failed test:
    /// ProcessEnvelopeAsync checks IsProcessedAsync before decrypting and stores the envelope
    /// (decrypted or not) afterwards via TryInsertAndMarkProcessedAsync, so both must succeed.
    /// Every other member must never run in this test, so it throws.</summary>
    private sealed class AcceptingEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("GetAsync must not run in this test.");

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ListPendingAsync must not run in this test.");

        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TryInsertAsync must not run in this test.");

        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TryMarkProcessedAsync must not run in this test.");

        public Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("DeleteAsync must not run in this test.");
    }

    /// <summary>An <see cref="IRemoteNoteArrivalSink"/> double for the corrupted-key test: no
    /// envelope is ever decrypted, so nothing should ever be notified.</summary>
    private sealed class NeverReachedArrivalSink : IRemoteNoteArrivalSink
    {
        public Task NotifyAsync(Guid messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("NotifyAsync must not run in this test.");
    }

    /// <summary>A real-time <see cref="IClock"/>: never actually read on the corrupted-key path
    /// (the failure happens before any envelope is decrypted), but <see cref="RemoteSyncService"/>
    /// requires a non-null one.</summary>
    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>An <see cref="IRandomSource"/> that always returns zero: never actually read on
    /// the corrupted-key path, but <see cref="PollBackoff"/> requires a non-null one.</summary>
    private sealed class ZeroRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
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
