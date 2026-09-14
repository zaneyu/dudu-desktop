namespace Dudu.Core;

public static class ProductInfo
{
    public const string Name = "Dudu Desktop";
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Fallback relay base URL used when the <c>DUDU_RELAY_BASE_URL</c> environment variable is
    /// unset. Baked in for the private v1 release so an installed copy needs no configuration;
    /// the environment variable still overrides it (see docs/release.md §3).
    /// </summary>
    public const string? DefaultRelayBaseUrl = "https://dudu-relay.zaneyu2005.workers.dev/";
}
