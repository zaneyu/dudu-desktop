using Dudu.Core;
using Dudu.Infrastructure.Remote;

namespace Dudu.App.Hosting;

/// <summary>
/// Resolves the relay base URL the app starts with. Deliberately free of any WinUI type so the
/// same source file compiles into tests/Dudu.Infrastructure.Tests (see that project's
/// &lt;Compile Include&gt; list) and the rules below are actually executed on the build host,
/// rather than only reviewed. Lifted out of
/// <c>WindowsCompanionProductionComposition</c> for review I9.
/// </summary>
internal static class RelayConfiguration
{
    /// <summary>The one documented way to point this build at a relay (see docs/privacy.md).</summary>
    internal const string BaseUrlEnvironmentVariable = "DUDU_RELAY_BASE_URL";

    /// <summary>
    /// Resolves the relay base URL from <see cref="BaseUrlEnvironmentVariable"/>, falling back to
    /// <see cref="ProductInfo.DefaultRelayBaseUrl"/>. Either value is used only when it parses as
    /// an absolute http/https URI; anything else -- unset, malformed, or a non-http(s) scheme
    /// such as <c>file://</c> -- is ignored, leaving <see cref="RelayOptions.BaseUrl"/> null so
    /// <c>AddDuduInfrastructure</c> registers <see cref="OfflinePairingService"/> and no relay is
    /// activated. That fallback is a supported configuration, not a failure: the Connection page
    /// says so through <see cref="Dudu.Core.Abstractions.PairingStatusReason.RelayNotConfigured"/>.
    /// </summary>
    /// <param name="readEnvironmentVariable">
    /// How to read an environment variable. Defaults to the real process environment; tests pass
    /// their own so they never mutate it.
    /// </param>
    internal static RelayOptions Resolve(Func<string, string?>? readEnvironmentVariable = null)
    {
        var read = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        if (!TryParseAbsoluteHttpUri(read(BaseUrlEnvironmentVariable), out var baseUrl))
        {
            TryParseAbsoluteHttpUri(ProductInfo.DefaultRelayBaseUrl, out baseUrl);
        }

        return new RelayOptions(baseUrl);
    }

    internal static bool TryParseAbsoluteHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}
