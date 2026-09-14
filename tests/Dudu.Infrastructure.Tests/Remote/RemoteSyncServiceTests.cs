using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Tests.Crypto;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class RemoteSyncServiceTests
{
    [Fact]
    public async Task Duplicate_poll_is_stored_and_presented_once_then_acked()
    {
        await using var fixture = await RemoteSyncFixture.WithSameEnvelopeReturnedTwiceAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);
        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Single(fixture.Envelopes);
        Assert.Single(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
    }

    [Fact]
    public async Task Received_envelope_remains_visible_and_consumable_after_poll()
    {
        await using var fixture = await RemoteSyncFixture.WithSameEnvelopeReturnedTwiceAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(
            [fixture.MessageId],
            (await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken))
                .Select(envelope => envelope.MessageId));
        Assert.True(await fixture.RealRepository.TryConsumeAsync(
            fixture.MessageId,
            DateTimeOffset.UtcNow,
            fixture.CancellationToken));
        Assert.Empty(await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken));
    }

    [Fact]
    public async Task Partial_registration_is_repaired_before_polling()
    {
        await using var fixture = await RemoteSyncFixture.WithPartialRegistrationAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(1, fixture.Relay.RegisterCallCount);
        Assert.Equal(1, fixture.Relay.PollCallCount);
    }

    [Fact]
    public async Task Ack_is_not_sent_when_local_transaction_fails()
    {
        await using var fixture = await RemoteSyncFixture.WithRepositoryFailureAsync();

        await Assert.ThrowsAsync<RemoteSyncException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Empty(fixture.AcknowledgedIds);
    }

    [Fact]
    public async Task Reveal_does_not_make_a_network_call_or_persist_plaintext()
    {
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync("private hello");

        var note = await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken);

        Assert.Equal("private hello", note.Text);
        Assert.Equal(0, fixture.Relay.RequestCount);
        Assert.DoesNotContain("private hello", fixture.ReadRawDatabaseText());
    }

    [Fact]
    public async Task Unauthorized_poll_flips_to_needs_repair_and_stops_polling()
    {
        await using var fixture = await RemoteSyncFixture.WithUnauthorizedPollAsync();

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
        Assert.True(fixture.Service.NeedsRepair);
    }

    [Theory]
    // Review C2: both payloads deserialize cleanly -- System.Text.Json binds JSON null onto a
    // non-nullable string member without complaint. The first is the one named in the review;
    // it happened to survive only because the reaction check ran first and rejected null as an
    // unknown reaction. The second is the one that actually reached payload.Text and threw a
    // NullReferenceException, past the old catch filter and out of the poll loop entirely.
    // Both must be treated as any other undecryptable envelope: kept as ciphertext, never
    // presented, acknowledged so they stop coming back.
    [InlineData("""{"kind":"note","text":null,"reaction":null}""")]
    [InlineData("""{"kind":"note","text":null,"reaction":"none"}""")]
    public async Task Null_payload_fields_are_acked_and_stored_without_plaintext(string rawPayloadJson)
    {
        await using var fixture = await RemoteSyncFixture.WithRawPayloadAsync(rawPayloadJson);

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Single(fixture.Envelopes);
        Assert.Empty(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-decrypt");
    }

    [Fact]
    public async Task Malformed_wire_field_is_acked_instead_of_killing_the_poll()
    {
        // Review I1: BuildStoredEnvelope (the Base64Url decode of the wire fields) used to run
        // outside ProcessEnvelopeAsync's try, so one unparsable field threw straight out of the
        // poll -- and, before the RunLoopAsync fix, out of the background loop as well.
        await using var fixture = await RemoteSyncFixture.WithMalformedCiphertextAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Empty(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-decrypt");
    }

    [Fact]
    public async Task Poll_loop_survives_an_unexpected_exception_instead_of_faulting()
    {
        // Review I1: an exception that is not a RemoteSyncException at all (here, a relay client
        // contract violation) used to fault the loop task. _loopTask then stayed non-null, so
        // StartAsync refused to start a replacement and StopAsync rethrew the stale fault: the
        // service looked started and polled nothing until the app restarted.
        await using var fixture = await RemoteSyncFixture.WithUnexpectedPollExceptionAsync();

        await fixture.Service.StartAsync(fixture.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => fixture.ReportedErrors.Any(error => error.Tag == "remote-sync-loop"),
                fixture.CancellationToken);

            Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-loop");
            Assert.True(fixture.Service.IsRunning, "the loop must still be running after an unexpected exception");
        }
        finally
        {
            // Must not rethrow the loop's exception.
            await fixture.Service.StopAsync(fixture.CancellationToken);
        }

        Assert.False(fixture.Service.IsRunning);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or ten seconds elapse.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    [Fact]
    public async Task Poll_loop_backs_off_after_an_outage_and_resets_on_the_next_success()
    {
        await using var fixture = await RemoteSyncFixture.WithOneTransientOutageAsync();

        await fixture.Service.StartAsync(fixture.CancellationToken);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (fixture.Relay.PollCallCount < 2 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, fixture.CancellationToken);
            }
        }
        finally
        {
            await fixture.Service.StopAsync(fixture.CancellationToken);
        }

        Assert.True(fixture.Relay.PollCallCount >= 2, "expected the loop to retry after the simulated outage");
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-poll");
    }

    /// <summary>
    /// Wires a real temp SQLite <see cref="Database"/> (see
    /// tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs's DatabaseFixture), an
    /// <see cref="InMemorySecretStore"/> pre-seeded with a genuine ECDH key so
    /// <see cref="CryptoFixture"/>-built envelopes actually decrypt, and a <see cref="FakeRelayClient"/>
    /// that never touches the network, to exercise <see cref="RemoteSyncService"/> end to end.
    /// </summary>
    private sealed class RemoteSyncFixture : IAsyncDisposable
    {
        // Matches Dudu.Infrastructure.Crypto.DesktopKeyService's internal SecretStoreKey constant.
        private const string DesktopPrivateKeySecretKey = "desktop-ecdh-private-v1";

        private readonly string _root;
        private readonly Database _database;

        private RemoteSyncFixture(
            string root,
            Database database,
            RemoteEnvelopeRepository realRepository,
            RecordingRemoteEnvelopeRepository recordingRepository,
            FakeRelayClient relay,
            FakeArrivalSink sink,
            InMemorySecretStore secretStore,
            byte[] recipientPublicKeySpki,
            string messageId,
            RemoteSyncService service,
            List<(string Tag, Exception Exception)> reportedErrors)
        {
            _root = root;
            _database = database;
            RealRepository = realRepository;
            Recording = recordingRepository;
            Relay = relay;
            Sink = sink;
            SecretStore = secretStore;
            RecipientPublicKeySpki = recipientPublicKeySpki;
            MessageId = messageId;
            Service = service;
            ReportedErrors = reportedErrors;
        }

        public RemoteEnvelopeRepository RealRepository { get; }
        public RecordingRemoteEnvelopeRepository Recording { get; }
        public FakeRelayClient Relay { get; }
        public FakeArrivalSink Sink { get; }
        public InMemorySecretStore SecretStore { get; }
        public byte[] RecipientPublicKeySpki { get; }
        public string MessageId { get; }
        public RemoteSyncService Service { get; }
        public DatabaseOptions Options => _database.Options;
        public List<(string Tag, Exception Exception)> ReportedErrors { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public IReadOnlyList<RemoteEnvelope> Envelopes => Recording.Inserted;
        public IReadOnlyList<Guid> Presentations => Sink.Presentations;
        public IReadOnlyList<string> AcknowledgedIds => Relay.AcknowledgedIds;

        public static async Task<RemoteSyncFixture> WithSameEnvelopeReturnedTwiceAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithRepositoryFailureAsync()
        {
            var fixture = await CreateAsync(useThrowingRepository: true);
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithPartialRegistrationAsync()
        {
            var fixture = await CreateAsync();
            fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("partial-device");
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithStoredEncryptedEnvelopeAsync(string text)
        {
            var fixture = await CreateAsync();
            var encrypted = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, text, fixture.MessageId);
            var stored = new RemoteEnvelope(
                encrypted.MessageId,
                Base64Url.DecodeFromChars(encrypted.Ciphertext),
                Base64Url.DecodeFromChars(encrypted.EphemeralPublicKey),
                Base64Url.DecodeFromChars(encrypted.Nonce),
                null,
                encrypted.DeliverAfterUtc,
                DateTimeOffset.UtcNow)
            {
                HkdfSalt = Base64Url.DecodeFromChars(encrypted.HkdfSalt),
                CreatedUtc = encrypted.CreatedUtc,
            };
            await fixture.RealRepository.TryInsertAsync(stored, TestContext.Current.CancellationToken);
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithRawPayloadAsync(string rawPayloadJson)
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptRawPayloadFor(
                fixture.RecipientPublicKeySpki,
                Encoding.UTF8.GetBytes(rawPayloadJson),
                fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithMalformedCiphertextAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            var wire = ToRelayEnvelope(envelope);
            fixture.Relay.PollResult = [wire with { Ciphertext = "not-base64url!!!" }];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithUnexpectedPollExceptionAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.PollException = () => new InvalidOperationException("unexpected relay-client failure");
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithUnauthorizedPollAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.PollException = () => new RelayUnauthorizedException();
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithOneTransientOutageAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.FailNextPolls(1);
            return fixture;
        }

        private static async Task<RemoteSyncFixture> CreateAsync(bool useThrowingRepository = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-remote-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var realRepository = new RemoteEnvelopeRepository(database);
            var recording = new RecordingRemoteEnvelopeRepository(realRepository);
            IRemoteEnvelopeRepository serviceRepository = useThrowingRepository
                ? new ThrowingRemoteEnvelopeRepository(recording)
                : recording;

            using var desktopKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var secretStore = new InMemorySecretStore();
            await secretStore.SetAsync(
                DesktopPrivateKeySecretKey,
                desktopKey.ExportPkcs8PrivateKey(),
                TestContext.Current.CancellationToken);
            var recipientPublicKeySpki = desktopKey.ExportSubjectPublicKeyInfo();

            var keyService = new Dudu.Infrastructure.Crypto.DesktopKeyService(secretStore);
            var relay = new FakeRelayClient();
            var sink = new FakeArrivalSink();
            // Must track real UtcNow (not an arbitrary fixed date): EnvelopeCrypto.Decrypt rejects
            // an envelope whose wire createdUtc is more than 5 minutes ahead of the clock it is
            // given, and CryptoFixture.EncryptFor always stamps createdUtc with the real clock.
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var backoff = new PollBackoff(new FixedFractionRandomSource(0));
            var messageId = Guid.NewGuid().ToString();
            var reportedErrors = new List<(string Tag, Exception Exception)>();

            var service = new RemoteSyncService(
                relay,
                serviceRepository,
                sink,
                secretStore,
                keyService,
                clock,
                backoff,
                (tag, exception) => reportedErrors.Add((tag, exception)));

            return new RemoteSyncFixture(
                root, database, realRepository, recording, relay, sink, secretStore, recipientPublicKeySpki, messageId, service,
                reportedErrors);
        }

        private static RelayEnvelope ToRelayEnvelope(Dudu.Infrastructure.Crypto.EncryptedEnvelope envelope) =>
            new(
                envelope.ProtocolVersion,
                envelope.MessageId,
                envelope.CreatedUtc,
                envelope.DeliverAfterUtc,
                envelope.EphemeralPublicKey,
                envelope.HkdfSalt,
                envelope.Nonce,
                envelope.Ciphertext);

        public string ReadRawDatabaseText()
        {
            var text = new StringBuilder();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = Options.DatabasePath + suffix;
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.Latin1);
                    text.Append(reader.ReadToEnd());
                }
            }

            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await _database.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedFractionRandomSource(double fraction) : IRandomSource
    {
        public int Next(int exclusiveMax) => (int)(fraction * exclusiveMax);
    }

    /// <summary>Records every envelope a real repository actually inserted (as opposed to a
    /// no-op duplicate), so tests can assert how many distinct envelopes were stored.</summary>
    private sealed class RecordingRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        private readonly IRemoteEnvelopeRepository _inner;

        public RecordingRemoteEnvelopeRepository(IRemoteEnvelopeRepository inner) => _inner = inner;

        public List<RemoteEnvelope> Inserted { get; } = [];

        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.GetAsync(messageId, cancellationToken);

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(cancellationToken);

        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            _inner.TryInsertAsync(envelope, cancellationToken);

        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.IsProcessedAsync(messageId, cancellationToken);

        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            _inner.TryMarkProcessedAsync(messageId, processedUtc, cancellationToken);

        public async Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken)
        {
            var inserted = await _inner.TryInsertAndMarkProcessedAsync(envelope, processedUtc, cancellationToken);
            if (inserted) Inserted.Add(envelope);
            return inserted;
        }

        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(messageId, cancellationToken);
    }

    /// <summary>Always throws from the one member <see cref="RemoteSyncService"/> relies on to
    /// persist a freshly decrypted envelope, simulating a local-transaction failure.</summary>
    private sealed class ThrowingRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        private readonly IRemoteEnvelopeRepository _inner;

        public ThrowingRemoteEnvelopeRepository(IRemoteEnvelopeRepository inner) => _inner = inner;

        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.GetAsync(messageId, cancellationToken);

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(cancellationToken);

        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            _inner.TryInsertAsync(envelope, cancellationToken);

        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.IsProcessedAsync(messageId, cancellationToken);

        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            _inner.TryMarkProcessedAsync(messageId, processedUtc, cancellationToken);

        public Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated repository failure");

        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(messageId, cancellationToken);
    }

    /// <summary>An <see cref="IRemoteNoteArrivalSink"/> test double recording each notification.
    /// Never records note text or reaction (the interface does not carry them).</summary>
    private sealed class FakeArrivalSink : IRemoteNoteArrivalSink
    {
        public List<Guid> Presentations { get; } = [];

        public Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
        {
            Presentations.Add(messageId);
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="IRelayClient"/> test double that never touches the network. Ack is
    /// idempotent (per the relay's own contract) so <see cref="AcknowledgedIds"/> records each
    /// distinct message id once regardless of how many times it is acknowledged.</summary>
    private sealed class FakeRelayClient : IRelayClient
    {
        private readonly HashSet<string> _ackedSet = new(StringComparer.Ordinal);
        private int _failuresRemaining;

        public int RequestCount { get; private set; }
        public int PollCallCount { get; private set; }
        public int RegisterCallCount { get; private set; }
        public IReadOnlyList<RelayEnvelope>? PollResult { get; set; }
        public Func<Exception>? PollException { get; set; }
        public List<string> AcknowledgedIds { get; } = [];

        public void FailNextPolls(int count) => _failuresRemaining = count;

        public Task<RelayRegistrationResult> RegisterAsync(string publicKeySpki, CancellationToken cancellationToken)
        {
            RequestCount++;
            RegisterCallCount++;
            return Task.FromResult(new RelayRegistrationResult(
                "device-1", "token-1", "ABC123", DateTimeOffset.UtcNow.AddMinutes(10)));
        }

        public Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new RelayDeviceInfo(DateTimeOffset.UtcNow, "fingerprint", 0));
        }

        public Task<RelayPairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new RelayPairingCode("ABC123", DateTimeOffset.UtcNow.AddMinutes(10)));
        }

        public Task<string> RotateKeyAsync(string publicKeySpki, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult("rotated-token");
        }

        public Task DeleteDeviceAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RelayEnvelope>> PollAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            PollCallCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new RelayUnavailableException("simulated outage");
            }

            if (PollException is not null)
            {
                throw PollException();
            }

            return Task.FromResult(PollResult ?? (IReadOnlyList<RelayEnvelope>)Array.Empty<RelayEnvelope>());
        }

        public Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_ackedSet.Add(messageId))
            {
                AcknowledgedIds.Add(messageId);
            }

            return Task.CompletedTask;
        }
    }
}
