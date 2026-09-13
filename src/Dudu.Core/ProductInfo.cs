namespace Dudu.Core;

public static class ProductInfo
{
    public const string Name = "Dudu Desktop";
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Fallback relay base URL used when the <c>DUDU_RELAY_BASE_URL</c> environment variable is
    /// unset. Left unset for now; Task 24's release runbook assigns the production relay URL.
    /// </summary>
    public const string? DefaultRelayBaseUrl = null;
}
