using System.Buffers.Text;
using System.Security.Cryptography;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Crypto;

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

    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private volatile PairingAvailability _state = PairingAvailability.Offline;

    public RemoteSyncService(
        IRelayClient relay,
        IRemoteEnvelopeRepository envelopes,
        IRemoteNoteArrivalSink sink,
        ISecretStore secretStore,
        DesktopKeyService keyService,
        IClock clock,
        PollBackoff backoff,
        Action<string, Exception>? reportError = null)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _reportError = reportError;
    }

    public PairingAvailability State => _state;

    public bool NeedsRepair => _state == PairingAvailability.NeedsRepair;

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
        }
        catch (RelayUnauthorizedException)
        {
            _state = PairingAvailability.NeedsRepair;
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
            if (_loopTask is not null)
            {
                return Task.CompletedTask;
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
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
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
            catch (RemoteSyncException exception)
            {
                _reportError?.Invoke("remote-sync-poll", exception);
                var delay = _backoff.NextDelay();
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
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

        var decrypted = true;
        try
        {
            EnvelopeCrypto.Decrypt(envelope, privateKeyPkcs8, _clock.UtcNow);
        }
        catch (Exception exception) when (exception is CryptographicException or EnvelopeValidationException)
        {
            decrypted = false;
            _reportError?.Invoke(
                "remote-sync-decrypt",
                new InvalidOperationException($"Envelope {wire.MessageId} failed to decrypt or validate."));
        }

        var stored = BuildStoredEnvelope(wire, _clock.UtcNow);
        try
        {
            await _envelopes.TryInsertAndMarkProcessedAsync(stored, _clock.UtcNow, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RemoteSyncException("Failed to store the received envelope locally.", exception);
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
    /// Revokes every sender session by rotating the desktop key: the relay discards queued
    /// ciphertext and every sender session tied to the old key as part of rotation.
    /// </summary>
    public async Task DisconnectSendersAsync(CancellationToken cancellationToken)
    {
        var keyMaterial = await _keyService.GetOrCreateAsync(cancellationToken);
        await _relay.RotateKeyAsync(keyMaterial.PublicKeySpkiBase64Url, cancellationToken);
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
        await _relay.RegisterAsync(keyMaterial.PublicKeySpkiBase64Url, cancellationToken);
    }

    private Task<byte[]?> GetDeviceIdAsync(CancellationToken cancellationToken) =>
        _secretStore.GetAsync(RelaySecretKeys.DeviceId, cancellationToken);

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
