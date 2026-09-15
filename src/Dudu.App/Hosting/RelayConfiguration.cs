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
    /// <summary>The one documented way to point this build at a relay (see docs/privacy.md).
    /// RELEASE OPT-IN: private release builds must ship with this variable UNSET so the baked-in
    /// production relay is used. Setting it is an explicit local-development opt-in (e.g. a
    /// loopback Worker under test) and must never be part of release handoff, installer testing,
    /// or any machine that is handed to someone else.</summary>
    internal const string BaseUrlEnvironmentVariable = "DUDU_RELAY_BASE_URL";

    /// <summary>
    /// Resolves the relay base URL from <see cref="BaseUrlEnvironmentVariable"/>, falling back to
    /// <see cref="ProductInfo.DefaultRelayBaseUrl"/>. Either value is used only when it parses as
    /// an absolute HTTPS URI, or explicit loopback HTTP for development; anything else -- unset,
    /// malformed, public HTTP, or a non-http(s) scheme
    /// such as <c>file://</c> -- is ignored, leaving <see cref="RelayOptions.BaseUrl"/> null so
    /// <c>AddDuduInfrastructure</c> registers <see cref="OfflinePairingService"/> and no relay is
    /// activated. That fallback is a supported configuration, not a failure: the Connection page
    /// says so through <see cref="Dudu.Core.Abstractions.PairingStatusReason.RelayNotConfigured"/>.
    /// </summary>
    /// <param name="readEnvironmentVariable">
    /// How to read an environment variable. Defaults to the real process environment; tests pass
    /// their own so they never mutate it.
    /// </param>
    /// <param name="logResolvedBaseUrl">
    /// Optional startup sink receiving the resolved relay origin (scheme + host + port only, e.g.
    /// <c>https://relay.example.test</c>) or <c>(relay not configured)</c>. The origin carries no
    /// secrets: the relay credential is a bearer token that never appears in the URL, and any path
    /// or query is stripped before logging.
    /// </param>
    internal static RelayOptions Resolve(
        Func<string, string?>? readEnvironmentVariable = null,
        Action<string>? logResolvedBaseUrl = null)
    {
        var read = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        if (!TryParseAbsoluteHttpUri(read(BaseUrlEnvironmentVariable), out var baseUrl))
        {
            TryParseAbsoluteHttpUri(ProductInfo.DefaultRelayBaseUrl, out baseUrl);
        }

        logResolvedBaseUrl?.Invoke(
            baseUrl?.GetLeftPart(UriPartial.Authority) ?? "(relay not configured)");

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
            && (parsed.Scheme == Uri.UriSchemeHttps
                || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}
