using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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

        _httpClient = httpClient;
        _secretStore = secretStore;
        _logger = logger ?? NullLogger<RelayClient>.Instance;
    }

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

        await _secretStore.SetAsync(
            RelaySecretKeys.DeviceId, Encoding.UTF8.GetBytes(body.DeviceId), cancellationToken);
        await _secretStore.SetAsync(
            RelaySecretKeys.DesktopToken, Encoding.UTF8.GetBytes(body.DesktopToken), cancellationToken);

        return new RelayRegistrationResult(
            body.DeviceId,
            body.DesktopToken,
            body.PairingCode,
            ParseUtc(body.PairingCodeExpiresUtc, "pairingCodeExpiresUtc"));
    }

    public async Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(HttpMethod.Get, "/v1/devices/current", cancellationToken);
        var body = await ReadAsync(response, RelayJsonContext.Default.GetCurrentDeviceResponseDto, cancellationToken);
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
        await _secretStore.SetAsync(
            RelaySecretKeys.DesktopToken, Encoding.UTF8.GetBytes(body.DesktopToken), cancellationToken);
        return body.DesktopToken;
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
        await SendAuthenticatedAsync(
            HttpMethod.Post,
            $"/v1/messages/{Uri.EscapeDataString(messageId)}/ack",
            cancellationToken);
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
        using var request = new HttpRequestMessage(method, new Uri(_baseUrl, path));

        if (requestBody is not null && requestTypeInfo is not null)
        {
            var json = JsonSerializer.Serialize(requestBody, requestTypeInfo);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (authenticated)
        {
            var tokenBytes = await _secretStore.GetAsync(RelaySecretKeys.DesktopToken, cancellationToken)
                ?? throw new RelayUnauthorizedException("No desktop token is stored.");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", Encoding.UTF8.GetString(tokenBytes));
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
