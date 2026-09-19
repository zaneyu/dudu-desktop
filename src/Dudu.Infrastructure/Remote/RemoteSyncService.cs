using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Logging;
using Dudu.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Drives the desktop's side of the relay protocol: lazy device registration, a background poll
/// loop with jittered backoff, per-envelope decrypt/store/notify/acknowledge, and on-demand reveal
/// of already-stored ciphertext. Registered as the app's <see cref="IPairingService"/> (via the
/// thin <see cref="RelayPairingService"/> adapter) once a relay base URL is configured.
/// </summary>
public sealed class RemoteSyncService : IAsyncDisposable
{
    /// <summary>
    /// Per-envelope wire-size bound (sum of the Base64URL field lengths, which approximate bytes).
    /// The relay caps a poll page at 48 KiB / 20 envelopes and a single envelope at ~8.6 KB, so a
    /// well-formed page can never trip this; anything larger is a poison envelope that is acked
    /// and skipped (advance past it) instead of stalling the queue or bloating local storage.
    /// </summary>
    internal const int MaxEnvelopeWireBytes = 12_288;

    /// <summary>Invalid (undecryptable) envelopes stored and reported per UTC day; the rest are
    /// acked and dropped. Envelopes are processed sequentially by one loop, so the counter needs
    /// no lock.</summary>
    internal const int InvalidEnvelopeDailyQuota = 3;

    private DateOnly _invalidEnvelopeDay;
    private int _invalidEnvelopeCount;

    private readonly IRelayClient _relay;
    private readonly IRemoteEnvelopeRepository _envelopes;
    private readonly IRemoteNoteArrivalSink _sink;
    private readonly ISecretStore _secretStore;
    private readonly DesktopKeyService _keyService;
    private readonly IClock _clock;
    private readonly PollBackoff _backoff;
    private readonly Action<string, Exception>? _reportError;
    private readonly ILogger<RemoteSyncService> _logger;

    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _stopTask;
    private bool _disposed;
    // B1: the cancellation token StartAsync was last invoked with (the app's own lifetime token
    // in production, not a short-lived per-click token). ForgetPairingLocallyAsync needs to stop
    // and later restart the loop around its local cleanup, and restarting it with anything other
    // than the token it originally ran under would tie the resumed loop to the wrong lifetime.
    private CancellationToken _hostCancellationToken;
    private volatile PairingAvailability _state = PairingAvailability.Offline;
    private volatile PairingStatusReason _statusReason = PairingStatusReason.None;

    public RemoteSyncService(
        IRelayClient relay,
        IRemoteEnvelopeRepository envelopes,
        IRemoteNoteArrivalSink sink,
        ISecretStore secretStore,
        DesktopKeyService keyService,
        IClock clock,
        PollBackoff backoff,
        Action<string, Exception>? reportError = null,
        ILogger<RemoteSyncService>? logger = null)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _reportError = reportError;
        _logger = logger ?? NullLogger<RemoteSyncService>.Instance;
    }

    public PairingAvailability State => _state;

    public bool NeedsRepair => _state == PairingAvailability.NeedsRepair;

    /// <summary>
    /// Why the loop is in <see cref="State"/>, when there is more to say than the coarse state.
    /// Surfaced to the Connection page through <see cref="RelayPairingService"/>.
    /// </summary>
    public PairingStatusReason StatusReason => _statusReason;

    /// <summary>True while a background poll loop is running. False before the first
    /// <see cref="StartAsync"/>, after <see cref="StopAsync"/>, and if the loop ever ends on
    /// its own -- which, after review C1/I1, only cancellation or an unauthorized relay can
    /// cause.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _loopTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Registers if needed and makes one cheap authenticated call to confirm the stored token
    /// still works, updating and returning <see cref="State"/>. Used by callers (e.g. the
    /// connection settings view) that need a live answer without waiting on the poll loop.
    /// </summary>
    public async Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureRegisteredAsync(cancellationToken);
            await _relay.GetDeviceAsync(cancellationToken);
            _state = PairingAvailability.Available;
            _statusReason = PairingStatusReason.None;
        }
        catch (RelayUnauthorizedException)
        {
            _state = PairingAvailability.NeedsRepair;
            _statusReason = PairingStatusReason.None;
        }
        catch (SecretStoreException exception)
        {
            // Audit finding F2: an unreadable secret (e.g. the DPAPI blob after a Windows
            // password reset or a profile move) will never succeed on retry with the same
            // secret, exactly like a dead bearer token. EnsureRegisteredAsync already set
            // NeedsRepair before rethrowing; restate it here so a probe racing a concurrent
            // repair still lands on the right state, and return normally instead of leaving the
            // caller with an unhandled exception and a stale Availability.
            _state = PairingAvailability.NeedsRepair;
            _statusReason = PairingStatusReason.None;
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
        }
        catch (RelayProtocolException exception)
        {
            // Review C1: an unreadable relay answer is a distinct, reportable condition, not
            // "offline" -- the Connection page says so instead of silently keeping a stale state.
            _statusReason = PairingStatusReason.RelayProtocolError;
            // P1: a failed probe must leave a diagnostic; otherwise a broken relay looks
            // identical to healthy-offline. Type name only — never the message, which for decode
            // failures can echo wire fields.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
        }
        catch (RelayUnavailableException exception)
        {
            // Leave the last known state: a transient outage does not mean pairing broke.
            // P1: still log the probe so an outage is distinguishable from healthy-offline.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
        }

        return _state;
    }

    /// <summary>Starts the background poll loop. Returns immediately; never blocks startup.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return Task.FromException(new ObjectDisposedException(nameof(RemoteSyncService)));
            }

            // A start racing a stop observes the stop as its result. It must
            // not silently create a replacement loop before the stop caller
            // has finished draining the old one.
            if (_stopTask is { IsCompleted: false } stopping)
            {
                return stopping;
            }

            if (_loopTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            if (_loopTask is { IsCompleted: true } completed)
            {
                // Review I1: a loop task that already finished -- including a faulted one --
                // must never wedge the service into "started but dead". Observe any fault so it
                // is not raised as unobserved at finalization, drop the old registration, and
                // fall through to start a fresh loop.
                _ = completed.Exception;
                _loopTask = null;
                _loopCts?.Dispose();
                _loopCts = null;
            }

            _hostCancellationToken = cancellationToken;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_stopTask?.IsCompleted == true)
            {
                _stopTask = null;
            }

            if (_stopTask is not null)
            {
                stopTask = _stopTask;
            }
            else
            {
                _loopCts?.Cancel();
                var loopTask = _loopTask;
                var loopCts = _loopCts;
                _loopTask = null;
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
                _ = DrainLoopAsync(loopTask, loopCts, completion);
                stopTask = completion.Task;
            }
        }

        // Do not return on a timeout: returning while the task is still alive
        // permits a concurrent start to resurrect polling during teardown.
        await stopTask;
    }

    private async Task DrainLoopAsync(
        Task? loopTask,
        CancellationTokenSource? loopCts,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            if (loopTask is not null)
            {
                try { await loopTask; }
                catch (Exception exception)
                {
                    // P1: an observed loop fault must leave a diagnostic instead of draining
                    // silently. Type name only — never the message, which can echo wire fields.
                    // Cancellation is normal shutdown, not a fault.
                    if (exception is not OperationCanceledException)
                    {
                        PrivacySafeLog.SyncLoopTerminal(_logger, exception.GetType().Name);
                    }
                }
            }
        }
        finally
        {
            loopCts?.Dispose();
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_loopCts, loopCts))
                {
                    _loopCts = null;
                }

                if (ReferenceEquals(_stopTask, completion.Task))
                {
                    _stopTask = null;
                }
            }

            completion.TrySetResult(true);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
                _backoff.Reset();
                // Steady cadence is jittered ±20% (not a fixed interval) so a fleet of healthy
                // desktops does not poll the relay in lockstep.
                await Task.Delay(_backoff.SteadyDelay(), cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RelayUnauthorizedException)
            {
                // State is already NeedsRepair (set by PollOnceAsync); stop polling until the
                // caller re-registers. P1: log the terminal exit so NeedsRepair is auditable in
                // logs as well as on the Connection page — in addition to any reportError call.
                PrivacySafeLog.SyncLoopTerminal(_logger, "needs-repair");
                return;
            }
            catch (SecretStoreException exception)
            {
                // H2: a permanently unreadable secret (EnsureRegisteredAsync's own read, or the
                // desktop key DesktopKeyService.GetOrCreateAsync just failed to parse) is a dead
                // end on retry, exactly like a dead bearer token -- the same secret will never
                // become readable on its own. State is already NeedsRepair (set by whichever call
                // threw); exit the loop the same way the 401 path does instead of backing off and
                // retrying forever against the same unreadable secret.
                _state = PairingAvailability.NeedsRepair;
                _reportError?.Invoke("remote-sync-secret-store", exception);
                PrivacySafeLog.SyncLoopTerminal(_logger, "needs-repair-secret-store");
                return;
            }
            catch (RelayProtocolException exception)
            {
                // Review C1: the relay answered with something this build cannot read -- most
                // importantly a page larger than BoundedJsonContent.MaxBytes, which the relay
                // will hand back byte-for-byte on every retry. Retrying it on the normal ramp
                // would poll, fail, and re-poll forever with nothing shown to the user. So it is
                // terminal for this iteration: report it, say so on the Connection page, and
                // wait the full capped backoff -- long enough not to hammer a broken relay, but
                // still a retry, so a relay that is fixed later recovers without a restart.
                _statusReason = PairingStatusReason.RelayProtocolError;
                _reportError?.Invoke("remote-sync-protocol", exception);
                // P1: log both the terminal backoff entry and the retry, in addition to the
                // reportError callback above. Fixed tags only — never wire content.
                PrivacySafeLog.SyncLoopTerminal(_logger, "protocol-backoff");
                if (!await TryDelayAsync(_backoff.MaxDelay(), cancellationToken))
                {
                    return;
                }

                PrivacySafeLog.SyncLoopRetry(_logger, "remote-sync-protocol");
            }
            catch (RemoteSyncException exception)
            {
                _reportError?.Invoke("remote-sync-poll", exception);
                if (!await TryDelayAsync(_backoff.NextDelay(), cancellationToken))
                {
                    return;
                }

                // P1: a failed iteration that retries must say so in the logs, not just via
                // the error reporter. Fixed tag matching the reportError tag.
                PrivacySafeLog.SyncLoopRetry(_logger, "remote-sync-poll");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Review I1: anything else -- a repository bug, a sink that throws, a
                // deserializer surprise -- used to fault the whole loop task, which then sat
                // "started" and dead until the app restarted. Treat it like any other failed
                // iteration instead.
                _reportError?.Invoke("remote-sync-loop", exception);
                if (!await TryDelayAsync(_backoff.NextDelay(), cancellationToken))
                {
                    return;
                }

                // P1: same retry diagnostic as above for the unexpected-exception path.
                PrivacySafeLog.SyncLoopRetry(_logger, "remote-sync-loop");
            }
        }
    }

    /// <summary>Waits <paramref name="delay"/>; returns false if the loop was cancelled while
    /// waiting, which is the loop's signal to exit.</summary>
    private static async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Registers if needed, polls once, and processes every returned envelope.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(cancellationToken);

        IReadOnlyList<RelayEnvelope> envelopes;
        try
        {
            envelopes = await _relay.PollAsync(cancellationToken);
        }
        catch (RelayUnauthorizedException)
        {
            _state = PairingAvailability.NeedsRepair;
            throw;
        }

        _state = PairingAvailability.Available;
        _statusReason = PairingStatusReason.None;

        var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
        try
        {
            // M2: one throwing envelope used to abort the whole batch, and -- because it is never
            // acked when that happens -- come back first on every subsequent poll, permanently
            // starving every envelope behind it (head-of-line blocking). Process every envelope in
            // the batch regardless of earlier failures, then report the failure(s) once the batch
            // ends so RunLoopAsync's existing backoff still applies. No attempt counter or
            // quarantine: a still-broken envelope simply fails again next poll.
            List<Exception>? failures = null;
            foreach (var envelope in envelopes)
            {
                try
                {
                    await ProcessEnvelopeAsync(envelope, keyMaterial.PrivateKeyPkcs8, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                    if (Guid.TryParse(envelope.MessageId, out var failedId))
                    {
                        // Exception type only -- never the message, matching the decrypt-failure
                        // logging convention elsewhere in this method.
                        PrivacySafeLog.EnvelopeRejected(_logger, failedId, "batch:" + exception.GetType().Name);
                    }
                }
            }

            if (failures is { Count: > 0 })
            {
                // A dead credential ends the whole poll rather than being aggregated away, so
                // RunLoopAsync's existing 401 handling (stop polling, converge on NeedsRepair)
                // still applies even when it surfaced partway through a batch. The same reasoning
                // extends to RunLoopAsync's other typed catches -- SecretStoreException,
                // RelayProtocolException, and RemoteSyncException -- since an AggregateException
                // matches none of them and would otherwise skip their backoff/status handling
                // entirely once more than one envelope fails in the same poll.
                Exception? significantFailure = failures.OfType<RelayUnauthorizedException>().FirstOrDefault();
                significantFailure ??= failures.OfType<SecretStoreException>().FirstOrDefault();
                significantFailure ??= failures.OfType<RelayProtocolException>().FirstOrDefault();
                significantFailure ??= failures.OfType<RemoteSyncException>().FirstOrDefault();
                if (significantFailure is not null)
                {
                    // M1: only the most significant failure is thrown, so every other
                    // per-envelope failure in the same batch (e.g. a SqliteException writing a
                    // note, hidden behind a more significant RelayProtocolException) would
                    // otherwise vanish from diagnostics entirely. Report them now, before the
                    // significant one is thrown.
                    foreach (var failure in failures)
                    {
                        if (!ReferenceEquals(failure, significantFailure))
                        {
                            _reportError?.Invoke("remote-sync-envelope", failure);
                        }
                    }

                    throw significantFailure;
                }

                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
        }
    }

    private async Task ProcessEnvelopeAsync(
        RelayEnvelope wire,
        byte[] privateKeyPkcs8,
        CancellationToken cancellationToken)
    {
        if (EstimateWireBytes(wire) > MaxEnvelopeWireBytes)
        {
            // Poison envelope: far larger than any well-formed page member. It can never decrypt
            // into a valid note (EnvelopeCrypto caps ciphertext at 6 KiB), so ack-and-advance past
            // it instead of letting one bloated row stall the whole queue behind it.
            _reportError?.Invoke(
                "remote-sync-oversize",
                new InvalidOperationException(
                    $"Envelope {wire.MessageId} exceeds the per-envelope size bound."));
            if (Guid.TryParse(wire.MessageId, out var oversizedId))
            {
                PrivacySafeLog.EnvelopeRejected(_logger, oversizedId, "oversize");
            }

            await AcknowledgeWithRepairTrackingAsync(wire.MessageId, cancellationToken);
            return;
        }

        if (await _envelopes.IsProcessedAsync(wire.MessageId, cancellationToken))
        {
            await AcknowledgeWithRepairTrackingAsync(wire.MessageId, cancellationToken);
            return;
        }

        var envelope = new EncryptedEnvelope(
            wire.ProtocolVersion,
            wire.MessageId,
            wire.CreatedUtc,
            wire.DeliverAfterUtc,
            wire.EphemeralPublicKey,
            wire.HkdfSalt,
            wire.Nonce,
            wire.Ciphertext);

        // Review C2/I1: decode and decrypt both run inside this try. BuildStoredEnvelope is the
        // Base64Url decode of the wire fields, so a malformed field used to throw here -- before
        // the catch below, outside any handler -- and take the whole poll loop down; and the old
        // catch filter (CryptographicException/EnvelopeValidationException only) let a null
        // payload field escape as a NullReferenceException. Widening the filter to "any
        // non-cancellation failure" fixed that, but went too far the other way (audit finding F1):
        // a transient failure unrelated to this envelope's content -- SQLite busy, IO, a secret
        // store hiccup -- is not "undecryptable" and must not be acked away, since ack is
        // irreversible. Only the three permanent, content-is-the-problem failures below take the
        // ack-and-discard path. Review H1: a bare CryptographicException is not one of them --
        // EnvelopeCrypto's own local private-key import, DeriveRawSecretAgreement, and the AesGcm
        // constructor can all throw it for reasons that have nothing to do with this envelope
        // (e.g. a locally corrupt key), and treating that as "this envelope is bad" would ack a
        // note away that could still be delivered once the local problem is fixed. Only
        // AuthenticationTagMismatchException -- the specific subtype AesGcm.Decrypt raises for a
        // genuinely tampered ciphertext -- means the envelope itself is the problem.
        // EnvelopeValidationException already wraps FormatException for the wire fields it decodes
        // itself, but BuildStoredEnvelope below decodes the same fields again with the raw
        // Base64Url API, so FormatException is listed explicitly too). Everything else -- and
        // OperationCanceledException, which was never caught here -- propagates so the note stays
        // on the relay and is retried next sync.
        var decrypted = true;
        var deferred = false;
        RemoteEnvelope? stored = null;
        try
        {
            EnvelopeCrypto.Decrypt(envelope, privateKeyPkcs8, _clock.UtcNow);
            stored = BuildStoredEnvelope(wire, _clock.UtcNow);
            deferred = IsDeliveryDeferred(wire.DeliverAfterUtc, _clock.UtcNow);
        }
        catch (Exception exception) when (exception is AuthenticationTagMismatchException
            or EnvelopeValidationException
            or FormatException)
        {
            decrypted = false;
            if (Guid.TryParse(wire.MessageId, out var messageId))
            {
                // Exception type only -- never the message, which for a decode failure can echo
                // the offending wire field back into the log.
                PrivacySafeLog.EnvelopeRejected(_logger, messageId, exception.GetType().Name);
            }

            // Quota: anyone holding a sender session can push junk. Keep (and report) only the
            // first few invalid envelopes per UTC day; beyond that, ack and drop without storing
            // so junk can neither fill the database nor flood the error reporter.
            var withinQuota = TryConsumeInvalidEnvelopeQuota(_clock.UtcNow, out var firstOverQuota);
            if (withinQuota)
            {
                _reportError?.Invoke(
                    "remote-sync-decrypt",
                    new InvalidOperationException(
                        $"Envelope {wire.MessageId} failed to decrypt or validate ({exception.GetType().Name})."));
                stored = TryBuildStoredEnvelope(wire);
            }
            else
            {
                if (firstOverQuota)
                {
                    _reportError?.Invoke(
                        "remote-sync-invalid-quota",
                        new InvalidOperationException(
                            $"More than {InvalidEnvelopeDailyQuota} invalid envelopes today; further ones are dropped."));
                }

                stored = null;
            }
        }

        var storedByThisPoll = false;
        if (deferred && stored is not null)
        {
            // Scheduled for the future: the relay normally withholds such envelopes until they are
            // due, but a skewed sender/relay clock can still deliver one early. Persist the
            // ciphertext WITHOUT marking it processed, and deliberately skip both notify and ack:
            // notifying now would surface the note before its scheduled time, and acking now would
            // delete the relay copy before it was ever presented. The relay redelivers after its
            // delivery claim lapses, and the due poll then stores-marks-notifies-acks as usual.
            try
            {
                await _envelopes.TryInsertAsync(stored, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new RemoteSyncException("Failed to stage a deferred envelope locally.", exception);
            }

            return;
        }

        if (stored is not null)
        {
            try
            {
                storedByThisPoll = await _envelopes.TryInsertAndMarkProcessedAsync(
                    stored,
                    _clock.UtcNow,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new RemoteSyncException("Failed to store the received envelope locally.", exception);
            }
        }

        if (decrypted && storedByThisPoll)
        {
            await _sink.NotifyAsync(Guid.ParseExact(wire.MessageId, "D"), cancellationToken);
        }

        await AcknowledgeWithRepairTrackingAsync(wire.MessageId, cancellationToken);
    }

    /// <summary>
    /// True when the envelope carries a parsable deliver-after timestamp later than now. An
    /// unparsable timestamp is treated as due: the relay already filters on its own clock, so a
    /// garbage value here is a malformed-but-present note, not a scheduling directive.
    /// </summary>
    private static bool IsDeliveryDeferred(string? deliverAfterUtc, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(deliverAfterUtc))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                deliverAfterUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deliverAfter))
        {
            return false;
        }

        return deliverAfter > now;
    }

    /// <summary>Counts one invalid envelope against today's (UTC) quota. Returns true while
    /// within quota; <paramref name="firstOverQuota"/> is true only for the first envelope past it.
    /// In-memory by design: a restart resets the count, which still bounds junk per process run.
    /// </summary>
    private bool TryConsumeInvalidEnvelopeQuota(DateTimeOffset now, out bool firstOverQuota)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (day != _invalidEnvelopeDay)
        {
            _invalidEnvelopeDay = day;
            _invalidEnvelopeCount = 0;
        }

        _invalidEnvelopeCount++;
        firstOverQuota = _invalidEnvelopeCount == InvalidEnvelopeDailyQuota + 1;
        return _invalidEnvelopeCount <= InvalidEnvelopeDailyQuota;
    }

    /// <summary>Approximate wire size of one envelope: Base64URL characters stand in for bytes.
    /// </summary>
    private static int EstimateWireBytes(RelayEnvelope wire) =>
        wire.MessageId.Length
        + wire.CreatedUtc.Length
        + (wire.DeliverAfterUtc?.Length ?? 0)
        + wire.EphemeralPublicKey.Length
        + wire.HkdfSalt.Length
        + wire.Nonce.Length
        + wire.Ciphertext.Length;

    /// <summary>
    /// Acknowledges one envelope, tracking credential death: an ack rejected with 401 means the
    /// stored bearer token is dead (same as a 401 on poll), so the service must report
    /// NeedsRepair rather than backing off and retrying with the same credential.
    /// </summary>
    private async Task AcknowledgeWithRepairTrackingAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await _relay.AcknowledgeAsync(messageId, cancellationToken);
        }
        catch (RelayUnauthorizedException)
        {
            _state = PairingAvailability.NeedsRepair;
            throw;
        }
    }

    /// <summary>Decrypts a previously stored envelope in memory. No network I/O.</summary>
    public async Task<RevealedRemoteNote> RevealAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        try
        {
            var stored = await _envelopes.GetAsync(messageId, cancellationToken)
                ?? throw new KeyNotFoundException("aiyo cant find that note anymore");

            if (stored.CreatedUtc is null)
            {
                // Rows written before migration 0003 (or any row missing the wire createdUtc
                // verbatim) cannot be re-authenticated: the AAD requires the exact original string.
                throw new RemoteSyncException("This note has no recorded timestamp and cannot be revealed.");
            }

            var envelope = new EncryptedEnvelope(
                ProtocolVersion: Dudu.Core.ProductInfo.ProtocolVersion,
                MessageId: stored.MessageId,
                CreatedUtc: stored.CreatedUtc,
                DeliverAfterUtc: stored.DeliverAfterUtc,
                EphemeralPublicKey: Base64Url.EncodeToString(stored.EphemeralPublicKey ?? []),
                HkdfSalt: Base64Url.EncodeToString(stored.HkdfSalt ?? []),
                Nonce: Base64Url.EncodeToString(stored.Nonce ?? []),
                Ciphertext: Base64Url.EncodeToString(stored.Ciphertext));

            var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
            try
            {
                // Freshness is judged at the moment the note was received, not now. The poll path
                // already enforced the createdUtc window with that same clock reading; re-checking it
                // against the current clock would make a note kept unsaved for 30+ days (or revealed
                // after a clock change) permanently unrevealable.
                var payload = EnvelopeCrypto.Decrypt(envelope, keyMaterial.PrivateKeyPkcs8, stored.ReceivedUtc);
                return new RevealedRemoteNote(payload.Text, payload.Reaction);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // P1: every reveal failure used to propagate with no diagnostic. Log the envelope id
            // with the exception type only — never the message, which for decode/decrypt failures
            // can echo the offending wire field back into the log.
            if (Guid.TryParse(messageId, out var envelopeId))
            {
                PrivacySafeLog.EnvelopeRejected(_logger, envelopeId, exception.GetType().Name);
            }
            else
            {
                PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            }

            throw;
        }
    }

    public async Task<PairingCodeResult> CreatePairingCodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureRegisteredAsync(cancellationToken);
            try
            {
                var code = await _relay.CreatePairingCodeAsync(cancellationToken);
                _state = PairingAvailability.Available;
                return new PairingCodeResult(PairingAvailability.Available, code.Code, code.ExpiresUtc);
            }
            catch (RelayUnauthorizedException exception)
            {
                _state = PairingAvailability.NeedsRepair;
                // P1: the NeedsRepair flip was previously silent in the logs. 401 plus the
                // exception type — never the code value or its expiry.
                PrivacySafeLog.SyncStateProbeFailed(_logger, 401, exception.GetType().Name);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not RelayUnauthorizedException)
        {
            // P1: transient pairing-code failures (unavailable, protocol, registration) used to
            // propagate with no diagnostic. Type name only — never wire fields.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            throw;
        }
    }

    public async Task<int> GetActiveSenderSessionCountAsync(CancellationToken cancellationToken)
    {
        byte[]? deviceId;
        try
        {
            deviceId = await GetDeviceIdAsync(cancellationToken);
        }
        catch (SecretStoreException)
        {
            // Audit finding F2: same NeedsRepair convergence as EnsureRegisteredAsync. This read
            // does not go through EnsureRegisteredAsync, so an unreadable secret must be handled
            // here too -- otherwise ConnectionViewModel.RefreshAsync (which calls this after
            // GetStateAsync already returned NeedsRepair) would throw before ever applying that
            // state to the bound properties, and the page would show a generic error instead.
            _state = PairingAvailability.NeedsRepair;
            return 0;
        }

        if (deviceId is null)
        {
            return 0;
        }

        try
        {
            var device = await _relay.GetDeviceAsync(cancellationToken);
            return device.ActiveSenderSessions;
        }
        catch (RelayUnauthorizedException)
        {
            // Includes the GetDevice-404 translation (device row gone server-side): the stored
            // credential is dead, so converge on NeedsRepair like every other authenticated call.
            _state = PairingAvailability.NeedsRepair;
            throw;
        }
        catch (SecretStoreException exception)
        {
            // H2: GetDeviceAsync authenticates with the bearer token, which it reads through the
            // secret store too -- the same dead end as the device-id read above, just reached one
            // call later. Same convergence and same "return 0 instead of throwing" contract as the
            // catch above, for the same ConnectionViewModel.RefreshAsync reason.
            _state = PairingAvailability.NeedsRepair;
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            return 0;
        }
    }

    /// <summary>
    /// Revokes every sender session: the relay deletes each paired sender session and the
    /// ciphertext still queued for them, and reissues this desktop's bearer token.
    /// <para>
    /// Review M3: despite the relay endpoint's name, this does NOT rotate the desktop's ECDH key
    /// pair -- it re-presents the same public key (see relay/src/routes/devices.ts
    /// <c>rotateDeviceKey</c>, which rotates the desktop token and revokes sessions). Rotating
    /// the key pair would make every already-stored envelope permanently unreadable, so it is
    /// deliberately not done here.
    /// </para>
    /// </summary>
    public async Task DisconnectSendersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
            try
            {
                await _relay.RotateKeyAsync(keyMaterial.PublicKeySpkiBase64Url, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // P1: rotation failures used to propagate with no diagnostic. Type name only — the
            // public key and any token material stay out of the logs.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Unpairs this desktop: deletes the remote device, then the local registration (device id,
    /// token, staged token). The next registration creates a fresh relay device.
    /// <para>
    /// A relay answer of 404 or 401 means the device or its credential is already gone, so local
    /// cleanup still runs. This is the repair path out of NeedsRepair. Transient failures
    /// (unavailable, protocol) propagate and leave local state untouched, so the call can be retried.
    /// </para>
    /// <para>
    /// The ECDH private key is kept unless <paramref name="destroyEncryptionKey"/> is true.
    /// Destroying it makes every stored, unrevealed note permanently unreadable, so only a caller
    /// that has the user's explicit confirmation may pass true.
    /// </para>
    /// </summary>
    public async Task RevokeDeviceAsync(
        CancellationToken cancellationToken,
        bool destroyEncryptionKey = false)
    {
        try
        {
            await _relay.DeleteDeviceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is RelayNotFoundException or RelayUnauthorizedException)
        {
            // Already gone server-side, or the credential is dead and can never delete it.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // P1: transient revoke failures used to propagate with no diagnostic, leaving local
            // state untouched but nothing in the logs. Type name only.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            throw;
        }

        // Device id first: it is the registration completion marker, so a failure part-way
        // leaves a state that EnsureRegisteredAsync treats as unregistered.
        try
        {
            await _secretStore.DeleteAsync(RelaySecretKeys.DeviceId, cancellationToken);
            await _secretStore.DeleteAsync(RelaySecretKeys.DesktopToken, cancellationToken);
            await _secretStore.DeleteAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);
            if (destroyEncryptionKey)
            {
                await _secretStore.DeleteAsync(DesktopKeyService.SecretStoreKey, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // P1: a local-cleanup failure during revoke also propagated silently. Type name only.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
            throw;
        }

        _state = PairingAvailability.Offline;
        _statusReason = PairingStatusReason.None;
    }

    /// <summary>
    /// Audit finding F2's escape hatch: forgets this desktop's pairing entirely on the local
    /// machine, tolerating a relay and/or secret store that are already broken.
    /// <para>
    /// <see cref="RevokeDeviceAsync"/> is the normal unpair path, but it needs a working bearer
    /// token to authenticate the relay delete call -- so it cannot get a user out of an unreadable
    /// secret store (e.g. after a Windows password reset invalidates the DPAPI master key), which
    /// is exactly the state that makes that same token unreadable. <see cref="ISecretStore.DeleteAsync"/>
    /// never decrypts anything (it only removes the stored ciphertext file), so the local half of
    /// this works even when every secret it touches is permanently unreadable.
    /// </para>
    /// <para>
    /// Review B1: this used to race the background poll loop -- an interleaved re-registration
    /// mid-delete could leave a device registered on the relay with its matching private key
    /// already gone, silently bricking every future note. The loop is stopped for the duration and
    /// every delete happens under <see cref="_registrationGate"/> (the same gate
    /// <see cref="EnsureRegisteredAsync"/> takes), so no concurrent caller can observe or create a
    /// half-forgotten state; the loop restarts afterward under the same host lifetime token it was
    /// originally started with.
    /// </para>
    /// <para>
    /// Review H3: the desktop's ECDH private key is destroyed only when it is already permanently
    /// unreadable (a dead DPAPI blob or an unparsable PKCS#8 blob) -- in that case every
    /// stored-but-unopened envelope is already undecryptable ciphertext, so those rows are purged
    /// too, leaving Love Notes clean instead of full of permanent "cannot finish that" errors.
    /// When the key is still readable it is kept: re-registration below reuses it, and every
    /// already-stored, unrevealed remote note stays revealable after re-pairing. Already-saved
    /// notes (<c>local_notes</c>) and relay-delivery dedup state (<c>processed_remote_messages</c>)
    /// are never touched either way.
    /// </para>
    /// <para>
    /// Review H4: a relay-side device delete is attempted first, best-effort, so the partner's
    /// device stops queueing notes to what is about to become a dead device. It never blocks or
    /// fails the local forget -- the whole reason this method exists is to recover when the relay
    /// and/or the credential needed to talk to it are already broken.
    /// </para>
    /// <para>
    /// Only a caller with the user's explicit confirmation may call this.
    /// </para>
    /// </summary>
    public async Task ForgetPairingLocallyAsync(CancellationToken cancellationToken)
    {
        var wasRunning = IsRunning;
        if (wasRunning)
        {
            await StopAsync(cancellationToken);
        }

        try
        {
            await _registrationGate.WaitAsync(cancellationToken);
            try
            {
                // H4 first: still has a chance at a live token, and must never block the local
                // forget that follows.
                await TryDeleteDeviceOnRelayBestEffortAsync(cancellationToken);

                // H3: decide the key's fate before mutating anything, so every local secret-store
                // delete below is unconditional once it starts.
                var keyIsReadable = await TryKeepDesktopKeyIfReadableAsync(cancellationToken);

                // B1: device id first, matching RevokeDeviceAsync's own convention -- it is the
                // registration completion marker, so any failure partway through the rest of this
                // sequence (including the key delete below) leaves a state EnsureRegisteredAsync
                // already treats as unregistered, never "registered but key-less".
                await _secretStore.DeleteAsync(RelaySecretKeys.DeviceId, cancellationToken);
                await _secretStore.DeleteAsync(RelaySecretKeys.DesktopToken, cancellationToken);
                await _secretStore.DeleteAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);

                if (!keyIsReadable)
                {
                    await _secretStore.DeleteAsync(DesktopKeyService.SecretStoreKey, cancellationToken);
                    await _envelopes.DeleteAllAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // P1: same diagnostic convention as every other local-cleanup failure. Type name only.
                PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
                throw;
            }
            finally
            {
                _registrationGate.Release();
            }

            _state = PairingAvailability.Offline;
            _statusReason = PairingStatusReason.None;
        }
        finally
        {
            if (wasRunning)
            {
                await StartAsync(_hostCancellationToken);
            }
        }
    }

    /// <summary>H4: stops the partner's device from queueing notes to what is about to become a
    /// dead device, but never blocks or fails the local forget path this is called from -- that
    /// path exists specifically for when the relay, or the credential needed to talk to it, is
    /// already broken.</summary>
    private async Task TryDeleteDeviceOnRelayBestEffortAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _relay.DeleteDeviceAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The bounded timeout fired, not the caller's own cancellation -- ignore, best-effort.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Offline relay, already-gone device, dead token, anything else -- all ignored.
            // Type name only, matching every other local-cleanup diagnostic in this class.
            PrivacySafeLog.SyncStateProbeFailed(_logger, 0, exception.GetType().Name);
        }
    }

    /// <summary>H3: true if the desktop's ECDH private key parses and is therefore still usable
    /// (re-registration will reuse it and every already-stored envelope stays revealable) -- in
    /// which case it must not be destroyed. False only when reading or parsing it threw
    /// <see cref="SecretStoreException"/>, meaning it is permanently unreadable and every envelope
    /// encrypted to it is already permanently undecryptable.</summary>
    private async Task<bool> TryKeepDesktopKeyIfReadableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
            // Same M3 rule as PollOnceAsync/EnsureRegisteredAsync: the PKCS#8 copy this call
            // owns never outlives the call.
            CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
            return true;
        }
        catch (SecretStoreException)
        {
            return false;
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        await _registrationGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                // Registration writes the token before the id, and the id is the completion marker.
                // Check both anyway so an installation left by an older/partial build self-heals on the
                // next startup instead of treating a device id without its bearer token as registered.
                if (await HasSecretAsync(RelaySecretKeys.DeviceId, cancellationToken)
                    && await HasSecretAsync(RelaySecretKeys.DesktopToken, cancellationToken))
                {
                    return;
                }

                var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
                try
                {
                    await _relay.RegisterAsync(keyMaterial.PublicKeySpkiBase64Url, cancellationToken);
                }
                finally
                {
                    // Same M3 rule as PollOnceAsync/RevealAsync/DisconnectSendersAsync: the PKCS#8 copy
                    // this call owns never outlives the call.
                    CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
                }
            }
            catch (SecretStoreException)
            {
                // Audit finding F2: an unreadable secret (DPAPI blob invalidated by a Windows
                // password reset or a profile move) is a dead end on retry, not a transient
                // hiccup -- the same secret will never become readable. Every caller of this
                // method (GetStateAsync, PollOnceAsync, CreatePairingCodeAsync) must land on
                // NeedsRepair instead of retrying forever or surfacing a generic error, so the
                // Connection page can offer the local-only "forget pairing" recovery.
                _state = PairingAvailability.NeedsRepair;
                throw;
            }
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    private Task<byte[]?> GetDeviceIdAsync(CancellationToken cancellationToken) =>
        _secretStore.GetAsync(RelaySecretKeys.DeviceId, cancellationToken);

    private async Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _secretStore.GetAsync(key, cancellationToken);
        if (value is null)
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(value);
        return true;
    }

    /// <summary>Builds the stored row for an envelope that already failed to decrypt. Returns
    /// null when even the Base64Url decode is impossible: that envelope cannot be persisted at
    /// all, so the caller skips the store and only acknowledges it.</summary>
    private RemoteEnvelope? TryBuildStoredEnvelope(RelayEnvelope wire)
    {
        try
        {
            return BuildStoredEnvelope(wire, _clock.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static RemoteEnvelope BuildStoredEnvelope(RelayEnvelope wire, DateTimeOffset receivedUtc) =>
        new(
            wire.MessageId,
            Base64Url.DecodeFromChars(wire.Ciphertext),
            Base64Url.DecodeFromChars(wire.EphemeralPublicKey),
            Base64Url.DecodeFromChars(wire.Nonce),
            null,
            wire.DeliverAfterUtc,
            receivedUtc)
        {
            HkdfSalt = Base64Url.DecodeFromChars(wire.HkdfSalt),
            CreatedUtc = wire.CreatedUtc,
        };

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposed = true;
        }

        await StopAsync(CancellationToken.None);
    }
}
