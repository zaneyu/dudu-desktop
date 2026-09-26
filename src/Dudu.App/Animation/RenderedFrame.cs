namespace Dudu.App.Animation;

/// <summary>
/// A composed premultiplied-BGRA frame whose bytes are borrowed until the presenter
/// operation that received it completes. Dispose returns the pooled buffer and makes
/// subsequent byte access fail; metadata remains available for diagnostics.
/// </summary>
public sealed class RenderedFrame : IDisposable
{
    private readonly IFrameBufferReleaser? _releaser;
    private byte[]? _buffer;

    internal RenderedFrame(
        byte[] buffer,
        int byteCount,
        int width,
        int height,
        int stride,
        float opacity,
        string source,
        TimeSpan semanticDuration,
        TimeSpan frameDuration,
        IReadOnlyList<Dudu.App.Overlay.PixelRect>? overlayHitRegions,
        long overlayGeometryVersion,
        Dudu.App.Animation.OverlaySurfaceSnapshot? overlaySurface,
        IFrameBufferReleaser? releaser)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (byteCount < 0 || byteCount > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (stride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        if (byteCount < checked(stride * height))
        {
            throw new ArgumentException("The frame buffer is smaller than stride multiplied by height.", nameof(byteCount));
        }

        if (float.IsNaN(opacity) || float.IsInfinity(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        if (semanticDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(semanticDuration));
        }

        if (frameDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        }

        _buffer = buffer;
        ByteCount = byteCount;
        Width = width;
        Height = height;
        Stride = stride;
        Opacity = opacity;
        Source = source;
        SemanticDuration = semanticDuration;
        FrameDuration = frameDuration;
        OverlayHitRegions = overlayHitRegions?.ToArray() ?? [];
        OverlayGeometryVersion = overlayGeometryVersion;
        OverlaySurface = overlaySurface;
        _releaser = releaser;
    }

    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            var buffer = GetBuffer();
            return buffer.AsMemory(0, ByteCount);
        }
    }

    public ReadOnlyMemory<byte> PremultipliedBgra => Bytes;

    public int ByteCount { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public float Opacity { get; }

    public string Source { get; }

    public TimeSpan SemanticDuration { get; }

    public TimeSpan FrameDuration { get; }

    /// <summary>Action regions in this frame's source-pixel coordinate space.
    /// The presenter snapshots and scales them with the exact frame it shows.</summary>
    public IReadOnlyList<Dudu.App.Overlay.PixelRect> OverlayHitRegions { get; }
    public long OverlayGeometryVersion { get; }

    /// <summary>Painted but not part of Dudu (the UK clock pill): clicks
    /// there pass through to the desktop, in this frame's source pixels.</summary>
    public Dudu.App.Overlay.PixelRect? ClickThroughRegion { get; init; }
    public Dudu.App.Animation.OverlaySurfaceSnapshot? OverlaySurface { get; }

    public bool IsDisposed => Volatile.Read(ref _buffer) is null;

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _releaser?.Release(buffer);
        }
    }

    private byte[] GetBuffer() =>
        Volatile.Read(ref _buffer)
        ?? throw new ObjectDisposedException(nameof(RenderedFrame));
}

internal interface IFrameBufferReleaser
{
    void Release(byte[] buffer);
}
