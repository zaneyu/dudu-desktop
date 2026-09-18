using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Authenticated HTTP client for the relay Worker. Owns exactly the desktop device's bearer
/// token (via <see cref="ISecretStore"/>) and the version-one wire contract; never logs a token,
/// key, pairing code, or body. See protocol/README.md for the routes this mirrors.
/// </summary>
public sealed class RelayClient : IRelayClient
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secretStore;
    private readonly Uri _baseUrl;
    private readonly ILogger<RelayClient> _logger;

    public RelayClient(
        HttpClient httpClient,
        ISecretStore secretStore,
        RelayOptions options,
        ILogger<RelayClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(options);
        _baseUrl = options.BaseUrl ?? throw new ArgumentException(
            "RelayOptions.BaseUrl must be set to use RelayClient.", nameof(options));
        if (!IsAllowedBaseUrl(_baseUrl))
        {
            throw new ArgumentException(
                "The relay base URL must use HTTPS unless it is an explicit loopback HTTP development URL.",
                nameof(options));
        }

        _httpClient = httpClient;
        _secretStore = secretStore;
        _logger = logger ?? NullLogger<RelayClient>.Instance;
    }

    private static bool IsAllowedBaseUrl(Uri value) =>
        value.IsAbsoluteUri
        && (value.Scheme == Uri.UriSchemeHttps
            || (value.Scheme == Uri.UriSchemeHttp && value.IsLoopback));

    public async Task<RelayRegistrationResult> RegisterAsync(string publicKeySpki, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeySpki);

        var response = await SendAsync(
            HttpMethod.Post,
            "/v1/devices/register",
            new RegisterDeviceRequestDto(publicKeySpki),
            RelayJsonContext.Default.RegisterDeviceRequestDto,
            authenticated: false,
            cancellationToken);

        var body = await ReadAsync(response, RelayJsonContext.Default.RegisterDeviceResponseDto, cancellationToken);
        if (string.IsNullOrWhiteSpace(body.DeviceId)
            || string.IsNullOrWhiteSpace(body.DesktopToken)
            || string.IsNullOrWhiteSpace(body.PairingCode))
        {
            throw new RelayProtocolException("The relay returned an incomplete registration response.");
        }
        var expiresUtc = ParseUtc(body.PairingCodeExpiresUtc, "pairingCodeExpiresUtc");

        await _secretStore.DeleteAsync(RelaySecretKeys.DeviceId, cancellationToken);
        await _secretStore.DeleteAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);

        // Write the token first and the device id last. The presence of the id is the
        // registration-complete marker, so it must never be persisted before its credential.
        // The UTF-8 copies are zeroed once stored: the secret store copies what it needs.
        var tokenBytes = Encoding.UTF8.GetBytes(body.DesktopToken);
        var deviceIdBytes = Encoding.UTF8.GetBytes(body.DeviceId);
        try
        {
            await _secretStore.SetAsync(
                RelaySecretKeys.DesktopToken, tokenBytes, cancellationToken);
            await _secretStore.SetAsync(
                RelaySecretKeys.DeviceId, deviceIdBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(deviceIdBytes);
        }

        return new RelayRegistrationResult(
            body.DeviceId,
            body.DesktopToken,
            body.PairingCode,
            expiresUtc);
    }

    public async Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken)
    {
        GetCurrentDeviceResponseDto body;
        try
        {
            var response = await SendAuthenticatedAsync(HttpMethod.Get, "/v1/devices/current", cancellationToken);
            body = await ReadAsync(response, RelayJsonContext.Default.GetCurrentDeviceResponseDto, cancellationToken);
        }
        catch (RelayNotFoundException)
        {
            // The device row is gone server-side (deleted or expired): the stored credential is
            // dead even though it is syntactically fine. Report it as unauthorized so callers
            // converge on NeedsRepair instead of a generic protocol error.
            throw new RelayUnauthorizedException("This device is no longer registered with the relay.");
        }

        return new RelayDeviceInfo(
            ParseUtc(body.CreatedUtc, "createdUtc"),
            body.PublicKeyFingerprint,
            body.ActiveSenderSessions);
    }

    public async Task<RelayPairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(HttpMethod.Post, "/v1/devices/pairing-code", cancellationToken);
        var body = await ReadAsync(response, RelayJsonContext.Default.CreatePairingCodeResponseDto, cancellationToken);
        return new RelayPairingCode(body.Code, ParseUtc(body.ExpiresUtc, "expiresUtc"));
    }

    public async Task<string> RotateKeyAsync(string publicKeySpki, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeySpki);

        var response = await SendAsync(
            HttpMethod.Post,
            "/v1/devices/current/rotate-key",
            new RotateDeviceKeyRequestDto(publicKeySpki),
            RelayJsonContext.Default.RotateDeviceKeyRequestDto,
            authenticated: true,
            cancellationToken);

        var body = await ReadAsync(response, RelayJsonContext.Default.RotateDeviceKeyResponseDto, cancellationToken);

        // Staged rotation: the relay has already rotated server-side (the previous active token
        // is dead), so the new token is written to the staging slot first and activated only
        // once the staging write has succeeded. If the staging write fails the new token is lost,
        // so the local registration is deleted and the next EnsureRegisteredAsync re-registers.
        // If only the activation write fails, the staged token survives: the next authenticated
        // request that gets a 401 promotes it (see SendAsync) and the device keeps its pairing.
        var newTokenBytes = Encoding.UTF8.GetBytes(body.DesktopToken);
        try
        {
            try
            {
                await _secretStore.SetAsync(
                    RelaySecretKeys.DesktopTokenStaging, newTokenBytes, CancellationToken.None);
            }
            catch (Exception)
            {
                await ForceReRegistrationAsync(CancellationToken.None);
                throw;
            }

            // Uncancellable: the server-side token is already rotated, so abandoning here would
            // only leave the recovery to the 401 promotion path.
            await _secretStore.SetAsync(
                RelaySecretKeys.DesktopToken, newTokenBytes, CancellationToken.None);

            try
            {
                await _secretStore.DeleteAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Non-fatal: a stale staging row equals the active token, so a later 401 promotes
                // it once, retries, and then drops it. P1: dedicated staging-cleanup event so the
                // leftover is visible without the token value — type name only.
                PrivacySafeLog.RelayStagingCleanupFailed(_logger, 0, exception.GetType().Name);
            }

            return body.DesktopToken;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newTokenBytes);
        }
    }

    public async Task DeleteDeviceAsync(CancellationToken cancellationToken)
    {
        await SendAuthenticatedAsync(HttpMethod.Delete, "/v1/devices/current", cancellationToken);
    }

    public async Task<IReadOnlyList<RelayEnvelope>> PollAsync(CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(HttpMethod.Get, "/v1/messages", cancellationToken);
        var body = await ReadAsync(response, RelayJsonContext.Default.GetMessagesResponseDto, cancellationToken);
        var envelopes = body.Messages
            .Select(message => new RelayEnvelope(
                message.ProtocolVersion,
                message.MessageId,
                message.CreatedUtc,
                message.DeliverAfterUtc,
                message.EphemeralPublicKey,
                message.HkdfSalt,
                message.Nonce,
                message.Ciphertext))
            .ToArray();
        PrivacySafeLog.PollCompleted(_logger, envelopes.Length);
        return envelopes;
    }

    public async Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        try
        {
            await SendAuthenticatedAsync(
                HttpMethod.Post,
                $"/v1/messages/{Uri.EscapeDataString(messageId)}/ack",
                cancellationToken);
        }
        catch (RelayNotFoundException)
        {
            // Already gone (expired between poll and ack, or acked by a concurrent poll): the
            // relay's ack contract is idempotent, so a 404 is success, not a failure.
        }
    }

    /// <summary>
    /// Deletes every local registration secret so the next
    /// <c>EnsureRegisteredAsync</c> performs a fresh registration. Best-effort: a failing delete
    /// must not mask the write failure that triggered this recovery, and a leftover device id
    /// without its token still self-heals (registration requires both secrets).
    /// </summary>
    private async Task ForceReRegistrationAsync(CancellationToken cancellationToken)
    {
        foreach (var key in new[]
            {
                RelaySecretKeys.DeviceId,
                RelaySecretKeys.DesktopToken,
                RelaySecretKeys.DesktopTokenStaging,
            })
        {
            try
            {
                await _secretStore.DeleteAsync(key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best effort (see above): cleanup never masks the rotation failure. P1: still
                // leaves a diagnostic by exception type — never token material.
                PrivacySafeLog.RelayStagingCleanupFailed(_logger, 0, exception.GetType().Name);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken) =>
        await SendAsync<object?>(method, path, null, null, authenticated: true, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest? requestBody,
        JsonTypeInfo<TRequest>? requestTypeInfo,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendOnceAsync(method, path, requestBody, requestTypeInfo, authenticated, cancellationToken);
        }
        catch (RelayUnauthorizedException) when (authenticated)
        {
            if (!await TryPromoteStagedTokenAsync(cancellationToken))
            {
                throw;
            }
        }

        // An interrupted rotation left the relay's new token only in the staging slot. It is now
        // active; retry exactly once. A second 401 propagates (callers report NeedsRepair).
        return await SendOnceAsync(method, path, requestBody, requestTypeInfo, authenticated, cancellationToken);
    }

    /// <summary>
    /// Promotes a staged desktop token to the active slot. Returns false when nothing is staged.
    /// The staging slot is cleared either way after a promotion attempt, so a 401 can trigger at
    /// most one retry.
    /// </summary>
    private async Task<bool> TryPromoteStagedTokenAsync(CancellationToken cancellationToken)
    {
        var staged = await _secretStore.GetAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);
        if (staged is null)
        {
            return false;
        }

        try
        {
            await _secretStore.SetAsync(RelaySecretKeys.DesktopToken, staged, cancellationToken);
            await _secretStore.DeleteAsync(RelaySecretKeys.DesktopTokenStaging, cancellationToken);
            // P1: dedicated promotion event so the 401-recovery path is auditable. Fixed label
            // plus status only — the staged token value itself is never logged.
            PrivacySafeLog.RelayStagingPromoted(_logger, 401, "promoted-staged-token");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(staged);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest? requestBody,
        JsonTypeInfo<TRequest>? requestTypeInfo,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildRequestUri(path));

        if (requestBody is not null && requestTypeInfo is not null)
        {
            var json = JsonSerializer.Serialize(requestBody, requestTypeInfo);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (authenticated)
        {
            var tokenBytes = await _secretStore.GetAsync(RelaySecretKeys.DesktopToken, cancellationToken)
                ?? throw new RelayUnauthorizedException("No desktop token is stored.");
            try
            {
                // The header value is a string HttpClient requires, so it unavoidably lingers
                // until GC; the fetched bytes are zeroed promptly to shrink the exposure window.
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", Encoding.UTF8.GetString(tokenBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            PrivacySafeLog.RelayFailed(_logger, 0, "timeout");
            throw new RelayUnavailableException("The relay request timed out.");
        }
        catch (HttpRequestException exception)
        {
            PrivacySafeLog.RelayFailed(_logger, 0, "network-error");
            throw new RelayUnavailableException("The relay could not be reached.", exception);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            PrivacySafeLog.RelayFailed(_logger, (int)response.StatusCode, "unauthorized");
            response.Dispose();
            throw new RelayUnauthorizedException();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 is routing information, not a body to parse: an unknown message id on ack, or
            // a device row that no longer exists. Surfaced distinctly so GetDeviceAsync can map a
            // deleted device to NeedsRepair while AcknowledgeAsync treats a vanished message as
            // idempotent success.
            PrivacySafeLog.RelayFailed(_logger, (int)response.StatusCode, "not-found");
            response.Dispose();
            throw new RelayNotFoundException();
        }

        if ((int)response.StatusCode >= 500)
        {
            PrivacySafeLog.RelayFailed(_logger, (int)response.StatusCode, "unavailable");
            response.Dispose();
            throw new RelayUnavailableException($"The relay returned status {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            PrivacySafeLog.RelayFailed(_logger, (int)response.StatusCode, "protocol");
            response.Dispose();
            throw new RelayProtocolException($"The relay returned status {(int)response.StatusCode}.");
        }

        return response;
    }

    /// <summary>
    /// Resolves an API route below the configured relay path prefix. The standard <see cref="Uri"/>
    /// constructor treats a route beginning with <c>/</c> as rooted at the host and silently drops
    /// any configured path, which breaks relays hosted behind a reverse-proxy prefix.
    /// </summary>
    private Uri BuildRequestUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Relay request paths must be absolute route paths.", nameof(path));
        }

        var basePath = _baseUrl.AbsolutePath.TrimEnd('/');
        var routePath = $"{basePath}/{path.TrimStart('/')}";
        var builder = new UriBuilder(_baseUrl)
        {
            Path = routePath,
        };
        return builder.Uri;
    }

    private async Task<TResponse> ReadAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> typeInfo,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            var bytes = await BoundedJsonContent.ReadBoundedAsync(response.Content, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize(bytes, typeInfo)
                    ?? throw new RelayProtocolException("The relay returned an empty body.");
            }
            catch (JsonException exception)
            {
                PrivacySafeLog.RelayFailed(_logger, (int)response.StatusCode, "malformed-body");
                throw new RelayProtocolException("The relay returned a malformed response.", exception);
            }
        }
    }

    private static DateTimeOffset ParseUtc(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new RelayProtocolException($"The relay returned an unparsable '{fieldName}' timestamp.");
        }

        return parsed;
    }
}
