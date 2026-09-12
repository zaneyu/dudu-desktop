using System.Net;
using System.Text;
using Dudu.AssetTool;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class MediaSecurityTests
{
    [Fact]
    public async Task Redirect_to_http_is_rejected_before_payload_download()
    {
        using var client = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://unsafe.example/image.gif") },
        }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => MediaDownloader.DownloadAsync(client, "https://safe.example/start", 1024, TestContext.Current.CancellationToken));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_with_logo_and_artwork_is_not_treated_as_media()
    {
        var html = Encoding.UTF8.GetBytes("<html><img src=logo.png><img src=artwork.gif></html>");
        using var client = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(html),
        }));

        var response = await MediaDownloader.DownloadAsync(client, "https://safe.example/page", 1024 * 1024, TestContext.Current.CancellationToken);

        Assert.False(MediaPayloadValidator.IsSupportedImage(response.Bytes, 1024 * 1024));
    }

    [Fact]
    public void Media_url_must_be_explicit_https()
    {
        Assert.Throws<InvalidDataException>(() => MediaDownloader.RequireHttps(""));
        Assert.Throws<InvalidDataException>(() => MediaDownloader.RequireHttps("http://example.test/image.gif"));
        Assert.Equal("https", MediaDownloader.RequireHttps("https://example.test/image.gif").Scheme);
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
