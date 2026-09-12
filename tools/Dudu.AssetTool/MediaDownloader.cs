namespace Dudu.AssetTool;

public sealed record MediaDownloadResult(byte[] Bytes, string? ContentType, Uri FinalUri);

public static class MediaDownloader
{
    public const int MaximumRedirects = 5;

    public static HttpClient CreateClient()
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public static async Task<MediaDownloadResult> DownloadAsync(
        HttpClient client,
        string url,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var current = RequireHttps(url);

        for (var redirect = 0; ; redirect++)
        {
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirect >= MaximumRedirects)
                {
                    throw new InvalidDataException($"Redirect limit of {MaximumRedirects} exceeded for {url}.");
                }

                if (response.Headers.Location is null)
                {
                    throw new InvalidDataException($"Redirect response from {current} has no Location header.");
                }

                current = RequireHttps(new Uri(current, response.Headers.Location));
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            {
                throw new InvalidDataException($"Payload exceeds the {maximumBytes} byte safety limit: {current}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (buffer.Length <= maximumBytes)
            {
                var read = await responseStream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            if (buffer.Length > maximumBytes)
            {
                throw new InvalidDataException($"Payload exceeds the {maximumBytes} byte safety limit: {current}");
            }

            return new MediaDownloadResult(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType, current);
        }
    }

    public static Uri RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"Source URL is not an absolute HTTPS URL: {url}");
        }

        return RequireHttps(uri);
    }

    public static Uri RequireHttps(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Only HTTPS source URLs are allowed: {uri}");
        }

        return uri;
    }
}
