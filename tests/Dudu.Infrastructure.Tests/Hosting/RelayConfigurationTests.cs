using Dudu.App.Hosting;
using Dudu.Core;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Review I9: the relay-resolution rule (environment override, else the baked-in default) must be
/// executable and tested rather than buried in a WinUI-only composition file.
/// <see cref="RelayConfiguration"/> is linked into this project (see the csproj) precisely so
/// these three cases run on every host.
/// </summary>
public sealed class RelayConfigurationTests
{
    [Fact]
    public void Unset_environment_variable_falls_back_to_the_baked_in_relay()
    {
        // The private v1 build bakes the production relay in so an installed copy needs no
        // configuration; the environment variable is only an override.
        Assert.NotNull(ProductInfo.DefaultRelayBaseUrl);

        var options = RelayConfiguration.Resolve(_ => null);

        Assert.Equal(new Uri(ProductInfo.DefaultRelayBaseUrl), options.BaseUrl);
    }

    [Theory]
    [InlineData("file://x")]
    [InlineData("ftp://relay.example.test/")]
    [InlineData("relay.example.test")]
    [InlineData("   ")]
    public void A_value_that_is_not_an_absolute_http_url_falls_back_to_the_baked_in_relay(string value)
    {
        var options = RelayConfiguration.Resolve(_ => value);

        Assert.Equal(new Uri(ProductInfo.DefaultRelayBaseUrl!), options.BaseUrl);
    }

    [Fact]
    public void A_valid_https_url_is_used_as_the_relay_base_url()
    {
        var options = RelayConfiguration.Resolve(name =>
            name == RelayConfiguration.BaseUrlEnvironmentVariable ? "https://relay.example.test/" : null);

        Assert.Equal(new Uri("https://relay.example.test/"), options.BaseUrl);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8787/")]
    [InlineData("http://localhost:8787/")]
    [InlineData("http://[::1]:8787/")]
    public void Explicit_loopback_http_is_allowed_for_development(string value)
    {
        var options = RelayConfiguration.Resolve(name =>
            name == RelayConfiguration.BaseUrlEnvironmentVariable ? value : null);

        Assert.Equal(new Uri(value), options.BaseUrl);
    }

    [Fact]
    public void Public_http_is_rejected_instead_of_being_used_for_relay_traffic()
    {
        var options = RelayConfiguration.Resolve(name =>
            name == RelayConfiguration.BaseUrlEnvironmentVariable
                ? "http://relay.example.test/"
                : null);

        Assert.Equal(new Uri(ProductInfo.DefaultRelayBaseUrl!), options.BaseUrl);
    }

    [Fact]
    public void Resolve_reports_the_configured_origin_without_secrets()
    {
        // Startup logging takes the origin only (scheme + host + port): the relay credential is
        // a bearer token that never appears in the URL, and any path or query is stripped.
        string? logged = null;
        var options = RelayConfiguration.Resolve(_ => null, origin => logged = origin);

        Assert.Equal(
            new Uri(ProductInfo.DefaultRelayBaseUrl!).GetLeftPart(UriPartial.Authority),
            logged);
        Assert.Equal(new Uri(ProductInfo.DefaultRelayBaseUrl!), options.BaseUrl);
    }

    [Fact]
    public void Resolve_reports_the_environment_override_origin()
    {
        string? logged = null;
        var options = RelayConfiguration.Resolve(
            name => name == RelayConfiguration.BaseUrlEnvironmentVariable
                ? "https://relay.example.test/some/path?query=1"
                : null,
            origin => logged = origin);

        Assert.Equal(new Uri("https://relay.example.test/some/path?query=1"), options.BaseUrl);
        Assert.Equal("https://relay.example.test", logged);
    }
}
