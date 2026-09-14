using System.Buffers.Text;
using System.Security.Cryptography;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Logging;
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
    private static readonly TimeSpan SteadyPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

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
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
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
        catch (RelayProtocolException)
        {
            // Review C1: an unreadable relay answer is a distinct, reportable condition, not
            // "offline" -- the Connection page says so instead of silently keeping a stale state.
            _statusReason = PairingStatusReason.RelayProtocolError;
        }
        catch (RelayUnavailableException)
        {
            // Leave the last known state: a transient outage does not mean pairing broke.
        }

        return _state;
    }

    /// <summary>Starts the background poll loop. Returns immediately; never blocks startup.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
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

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loopTask;
        lock (_lifecycleGate)
        {
            _loopCts?.Cancel();
            loopTask = _loopTask;
            _loopTask = null;
        }

        if (loopTask is null)
        {
            return;
        }

        try
        {
            await loopTask.WaitAsync(StopTimeout, CancellationToken.None);
        }
        catch (Exception)
        {
            // Shutdown never fails. A TimeoutException (the loop is mid-delay), an
            // OperationCanceledException, or -- review I1 -- a stale fault left by an earlier
            // loop iteration are all expected here; a fault was already handed to _reportError
            // when it happened, and awaiting it here is only to observe it.
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
                await Task.Delay(SteadyPollInterval, cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RelayUnauthorizedException)
            {
                // State is already NeedsRepair (set by PollOnceAsync); stop polling until the
                // caller re-registers.
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
                if (!await TryDelayAsync(_backoff.MaxDelay(), cancellationToken))
                {
                    return;
                }
            }
            catch (RemoteSyncException exception)
            {
                _reportError?.Invoke("remote-sync-poll", exception);
                if (!await TryDelayAsync(_backoff.NextDelay(), cancellationToken))
                {
                    return;
                }
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
            foreach (var envelope in envelopes)
            {
                await ProcessEnvelopeAsync(envelope, keyMaterial.PrivateKeyPkcs8, cancellationToken);
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
        if (await _envelopes.IsProcessedAsync(wire.MessageId, cancellationToken))
        {
            await _relay.AcknowledgeAsync(wire.MessageId, cancellationToken);
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
        // payload field escape as a NullReferenceException. Any non-cancellation failure now
        // means the same thing to this method: undecryptable. Store the ciphertext without
        // plaintext, do not notify, and acknowledge, so a poison message drains instead of being
        // redelivered forever.
        var decrypted = true;
        RemoteEnvelope? stored = null;
        try
        {
            EnvelopeCrypto.Decrypt(envelope, privateKeyPkcs8, _clock.UtcNow);
            stored = BuildStoredEnvelope(wire, _clock.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            decrypted = false;
            _reportError?.Invoke(
                "remote-sync-decrypt",
                new InvalidOperationException(
                    $"Envelope {wire.MessageId} failed to decrypt or validate ({exception.GetType().Name})."));
            if (Guid.TryParse(wire.MessageId, out var messageId))
            {
                // Exception type only -- never the message, which for a decode failure can echo
                // the offending wire field back into the log.
                PrivacySafeLog.EnvelopeRejected(_logger, messageId, exception.GetType().Name);
            }

            stored = TryBuildStoredEnvelope(wire);
        }

        if (stored is not null)
        {
            try
            {
                await _envelopes.TryInsertAndMarkProcessedAsync(stored, _clock.UtcNow, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new RemoteSyncException("Failed to store the received envelope locally.", exception);
            }
        }

        if (decrypted)
        {
            await _sink.NotifyAsync(Guid.ParseExact(wire.MessageId, "D"), cancellationToken);
        }

        await _relay.AcknowledgeAsync(wire.MessageId, cancellationToken);
    }

    /// <summary>Decrypts a previously stored envelope in memory. No network I/O.</summary>
    public async Task<RevealedRemoteNote> RevealAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

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
            var payload = EnvelopeCrypto.Decrypt(envelope, keyMaterial.PrivateKeyPkcs8);
            return new RevealedRemoteNote(payload.Text, payload.Reaction);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.PrivateKeyPkcs8);
        }
    }

    public async Task<PairingCodeResult> CreatePairingCodeAsync(CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(cancellationToken);
        try
        {
            var code = await _relay.CreatePairingCodeAsync(cancellationToken);
            _state = PairingAvailability.Available;
            return new PairingCodeResult(PairingAvailability.Available, code.Code, code.ExpiresUtc);
        }
        catch (RelayUnauthorizedException)
        {
            _state = PairingAvailability.NeedsRepair;
            throw;
        }
    }

    public async Task<int> GetActiveSenderSessionCountAsync(CancellationToken cancellationToken)
    {
        if (await GetDeviceIdAsync(cancellationToken) is null)
        {
            return 0;
        }

        var device = await _relay.GetDeviceAsync(cancellationToken);
        return device.ActiveSenderSessions;
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

    /// <summary>Permanently unpairs this desktop: deletes the remote device, then all local
    /// pairing state (token, device id, and the desktop's own key material).</summary>
    public async Task RevokeDeviceAsync(CancellationToken cancellationToken)
    {
        await _relay.DeleteDeviceAsync(cancellationToken);
        await _secretStore.DeleteAsync(RelaySecretKeys.DeviceId, cancellationToken);
        await _secretStore.DeleteAsync(RelaySecretKeys.DesktopToken, cancellationToken);
        await _secretStore.DeleteAsync(DesktopKeyService.SecretStoreKey, cancellationToken);
        _state = PairingAvailability.Offline;
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (await GetDeviceIdAsync(cancellationToken) is not null)
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

    private Task<byte[]?> GetDeviceIdAsync(CancellationToken cancellationToken) =>
        _secretStore.GetAsync(RelaySecretKeys.DeviceId, cancellationToken);

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
        await StopAsync(CancellationToken.None);
        _loopCts?.Dispose();
    }
}
