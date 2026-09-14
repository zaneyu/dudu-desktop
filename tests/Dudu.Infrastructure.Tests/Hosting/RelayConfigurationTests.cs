using Dudu.App.Hosting;
using Dudu.Core;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Review I9: running with no relay is a supported, documented configuration, so the rule that
/// decides it must be executable and tested rather than buried in a WinUI-only composition file.
/// <see cref="RelayConfiguration"/> is linked into this project (see the csproj) precisely so
/// these three cases run on every host.
/// </summary>
public sealed class RelayConfigurationTests
{
    [Fact]
    public void Unset_environment_variable_leaves_the_app_offline()
    {
        // ProductInfo.DefaultRelayBaseUrl is deliberately null in this build, so an unset
        // variable is the shipping default: fully local, no relay.
        Assert.Null(ProductInfo.DefaultRelayBaseUrl);

        var options = RelayConfiguration.Resolve(_ => null);

        Assert.Null(options.BaseUrl);
    }

    [Theory]
    [InlineData("file://x")]
    [InlineData("ftp://relay.example.test/")]
    [InlineData("relay.example.test")]
    [InlineData("   ")]
    public void A_value_that_is_not_an_absolute_http_url_leaves_the_app_offline(string value)
    {
        var options = RelayConfiguration.Resolve(_ => value);

        Assert.Null(options.BaseUrl);
    }

    [Fact]
    public void A_valid_https_url_is_used_as_the_relay_base_url()
    {
        var options = RelayConfiguration.Resolve(name =>
            name == RelayConfiguration.BaseUrlEnvironmentVariable ? "https://relay.example.test/" : null);

        Assert.Equal(new Uri("https://relay.example.test/"), options.BaseUrl);
    }
}
