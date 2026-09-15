using System.Text.Json.Serialization;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Secret-store key names shared between <see cref="RelayClient"/> (which writes them) and
/// <see cref="RemoteSyncService"/> (which reads the device id to decide whether registration is
/// needed). Never logged.
/// </summary>
internal static class RelaySecretKeys
{
    public const string DesktopToken = "relay-desktop-token-v1";
    public const string DeviceId = "relay-device-id-v1";

    /// <summary>
    /// Staging slot for a freshly rotated bearer token: the new token is written here first and
    /// copied to <see cref="DesktopToken"/> only once the staging write has succeeded, so a failed
    /// rotation never leaves a half-written active credential. Dots are avoided because the DPAPI
    /// store restricts key names to <c>^[a-z0-9-]{1,64}$</c>.
    /// </summary>
    public const string DesktopTokenStaging = "relay-desktop-token-v1-staging";
}

internal sealed record RegisterDeviceRequestDto(
    [property: JsonPropertyName("publicKey")] string PublicKey);

internal sealed record RegisterDeviceResponseDto(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("desktopToken")] string DesktopToken,
    [property: JsonPropertyName("pairingCode")] string PairingCode,
    [property: JsonPropertyName("pairingCodeExpiresUtc")] string PairingCodeExpiresUtc);

internal sealed record GetCurrentDeviceResponseDto(
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("publicKeyFingerprint")] string PublicKeyFingerprint,
    [property: JsonPropertyName("activeSenderSessions")] int ActiveSenderSessions);

internal sealed record RotateDeviceKeyRequestDto(
    [property: JsonPropertyName("publicKey")] string PublicKey);

internal sealed record RotateDeviceKeyResponseDto(
    [property: JsonPropertyName("desktopToken")] string DesktopToken);

internal sealed record CreatePairingCodeResponseDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("expiresUtc")] string ExpiresUtc);

internal sealed record RelayEnvelopeDto(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("deliverAfterUtc")] string? DeliverAfterUtc,
    [property: JsonPropertyName("ephemeralPublicKey")] string EphemeralPublicKey,
    [property: JsonPropertyName("hkdfSalt")] string HkdfSalt,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ciphertext")] string Ciphertext);

internal sealed record GetMessagesResponseDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<RelayEnvelopeDto> Messages);

[JsonSerializable(typeof(RegisterDeviceRequestDto))]
[JsonSerializable(typeof(RegisterDeviceResponseDto))]
[JsonSerializable(typeof(GetCurrentDeviceResponseDto))]
[JsonSerializable(typeof(RotateDeviceKeyRequestDto))]
[JsonSerializable(typeof(RotateDeviceKeyResponseDto))]
[JsonSerializable(typeof(CreatePairingCodeResponseDto))]
[JsonSerializable(typeof(GetMessagesResponseDto))]
internal partial class RelayJsonContext : JsonSerializerContext
{
}
