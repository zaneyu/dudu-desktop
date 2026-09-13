namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Configuration for the relay integration. A null <see cref="BaseUrl"/> means no relay is
/// configured, and <c>AddDuduInfrastructure</c> falls back to <see cref="OfflinePairingService"/>.
/// </summary>
public sealed record RelayOptions(Uri? BaseUrl);
