using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Reads an HTTP response body into memory with a hard upper bound, so a misbehaving or
/// compromised relay cannot force the desktop client to buffer an unbounded amount of data
/// before (or instead of) returning a protocol error. This bound is desktop-only: it governs how
/// <see cref="RelayClient"/> reads the relay's responses, not how the relay itself reads
/// sender-facing request bodies (those are bounded separately, server-side).
/// </summary>
internal static class BoundedJsonContent
{
    /// <summary>The maximum number of bytes <see cref="ReadBoundedAsync"/> will buffer. The
    /// outer <see cref="HttpClient.MaxResponseContentBufferSize"/> (256 KiB) still bounds what
    /// the underlying transport will buffer before this check ever runs.</summary>
    public const int MaxBytes = 65_536;

    /// <summary>
    /// Reads at most <see cref="MaxBytes"/> bytes from <paramref name="content"/>. If the
    /// response declares (via <c>Content-Length</c>) or turns out to contain more than that,
    /// this throws <see cref="RelayProtocolException"/> without ever attempting to deserialize
    /// the body.
    /// </summary>
    public static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength is { } declaredLength && declaredLength > MaxBytes)
        {
            throw new RelayProtocolException("The relay response exceeded the maximum allowed size.");
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + bytesRead > MaxBytes)
            {
                throw new RelayProtocolException("The relay response exceeded the maximum allowed size.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return buffer.ToArray();
    }
}
