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
}
