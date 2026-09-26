using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dudu.App.Overlay;
using Dudu.Core.Assets;
using SkiaSharp;

namespace Dudu.App.Animation;

public sealed class SkiaFrameComposer : IDisposable, IFrameBufferReleaser
{
    private const int BytesPerPixel = 4;
    private const int MaxDimension = 4096;
    private const int MaxDecodedBitmapCount = 512;
    private const long MaxDecodedBitmapBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private static readonly SKSamplingOptions SamplingOptions = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private readonly Dictionary<string, SKImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _imageCacheBytes = new(StringComparer.OrdinalIgnoreCase);
    // Least-recently-used order for _imageCache: front (First) is the next
    // eviction candidate, back (Last) is the most recently touched entry.
    private readonly LinkedList<string> _imageCacheLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _imageCacheLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fullPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceCache = new(StringComparer.Ordinal);
    private SKBitmap? _surface;
    private SKCanvas? _canvas;
    private SKPaint? _paint;
    private byte[]? _reusableBuffer;
    private AssetPack? _pack;
    private OverlayActionSurfaceController? _actionSurface;
    private OverlaySurfacePalette? _overlayPalette;
    private long _decodedBitmapBytes;
    private int _disposeCount;
    private bool _disposed;

    public SkiaFrameComposer()
    {
    }

    public SkiaFrameComposer(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ValidatePackLimits(pack);
        _pack = pack;
    }

    public AssetPack? Pack
    {
        get
        {
            lock (_gate)
            {
                return _pack;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Raised whenever an overlay interaction changes without a new
    /// animation frame.  The engine uses it to repaint the existing pet frame
    /// immediately, including reduced-motion instructions and errors.</summary>
    public event EventHandler? RepaintRequested;

    public void SetActionSurface(OverlayActionSurfaceController? actionSurface)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_actionSurface, actionSurface)) return;
            if (_actionSurface is not null) _actionSurface.Changed -= OnActionSurfaceChanged;
            _actionSurface = actionSurface;
            if (_actionSurface is not null) _actionSurface.Changed += OnActionSurfaceChanged;
        }

        RequestRepaint();
    }

    public void SetOverlayPalette(OverlaySurfacePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        lock (_gate)
        {
            ThrowIfDisposed();
            _overlayPalette = palette;
        }

        RequestRepaint();
    }

    public void SetPack(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_pack, pack))
            {
                return;
            }

            ValidatePackLimits(pack);
            DisposeDecodedBitmaps();
            DisposeSurface();
            ReturnReusableBuffer();
            _pack = pack;
        }
    }

    public RenderedFrame Compose(
        AssetPack pack,
        AssetAnimation animation,
        AssetFrame frame,
        double scale = 1d,
        float opacity = 1f,
        TimeSpan semanticDuration = default,
        TimeSpan frameDuration = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(frame.File);
        ValidateCompositionInputs(pack, animation, frame, scale, opacity, semanticDuration, frameDuration);

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsurePack(pack);
            var dimensions = ValidateDimensions(animation.NominalSize, scale);
            var source = GetSource(pack, frame.File);
            var image = GetImage(pack, frame.File);
            EnsureSurface(dimensions.Width, dimensions.Height);
            var output = _surface!;

            _canvas!.Clear(SKColors.Transparent);
            _paint!.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * byte.MaxValue));
            var destination = new SKRect(0, 0, dimensions.Width, dimensions.Height);
            // DrawImage, not DrawBitmap: SkiaSharp's DrawBitmap wraps the bitmap in a
            // temporary SKImage on every call, which the 300-frame allocation gate
            // in AnimationEngineTests measured at about 100 bytes per frame.
            _canvas.DrawImage(image, destination, SamplingOptions, _paint);
            OverlaySurfaceSnapshot? overlaySnapshot = null;
            if (_actionSurface is not null)
            {
                overlaySnapshot = _actionSurface.CreateRenderSnapshot(
                    new PixelSize(dimensions.Width, dimensions.Height));
                OverlaySurfaceRenderer.Draw(
                    _canvas,
                    overlaySnapshot,
                    _overlayPalette);
            }

            var stride = output.RowBytes;
            var byteCount = checked(stride * dimensions.Height);
            var buffer = RentBuffer(byteCount);
            try
            {
                Marshal.Copy(output.GetPixels(), buffer, 0, byteCount);
                return new RenderedFrame(
                    buffer,
                    byteCount,
                    dimensions.Width,
                    dimensions.Height,
                    stride,
                    opacity,
                    source,
                    semanticDuration,
                    frameDuration,
                    overlaySnapshot?.Actions.Select(action => action.HitRegion).ToArray(),
                    overlaySnapshot?.GeometryVersion ?? 0,
                    overlaySnapshot,
                    this);
            }
            catch
            {
                Release(buffer);
                throw;
            }
        }
    }

    public RenderedFrame Compose(
        AssetPack pack,
        AssetAnimation animation,
        int frameIndex,
        double scale = 1d,
        float opacity = 1f,
        TimeSpan semanticDuration = default)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if ((uint)frameIndex >= (uint)animation.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        var frame = animation.Frames[frameIndex];
        return Compose(
            pack,
            animation,
            frame,
            scale,
            opacity,
            semanticDuration,
            TimeSpan.FromMilliseconds(frame.DurationMs));
    }

    public RenderedFrame Compose(
        AssetPack pack,
        string relativePath,
        PixelSize nominalSize,
        double scale = 1d,
        float opacity = 1f,
        TimeSpan semanticDuration = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var animation = new AssetAnimation
        {
            Frames = [new AssetFrame { File = relativePath, DurationMs = 1 }],
            NominalSize = nominalSize,
            Anchor = new PixelPoint(0, 0),
        };
        return Compose(pack, animation, 0, scale, opacity, semanticDuration);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCount++;
            if (_actionSurface is not null) _actionSurface.Changed -= OnActionSurfaceChanged;
            _actionSurface = null;
            _overlayPalette = null;
            RepaintRequested = null;
            DisposeDecodedBitmaps();
            DisposeSurface();
            ReturnReusableBuffer();
            _pack = null;
        }
    }

    void IFrameBufferReleaser.Release(byte[] buffer) => Release(buffer);

    private void OnActionSurfaceChanged(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        RequestRepaint();
    }

    private void RequestRepaint()
    {
        var handlers = RepaintRequested;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().OfType<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // A repaint request is advisory. One failing listener must
                // not prevent the overlay or composer from staying usable.
                Trace.TraceError("Dudu repaint listener failed: {0}", exception);
            }
        }
    }

    private void Release(byte[] buffer)
    {
        lock (_gate)
        {
            if (_disposed || _reusableBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }

            _reusableBuffer = buffer;
        }
    }

    private void EnsurePack(AssetPack pack)
    {
        if (ReferenceEquals(_pack, pack))
        {
            return;
        }

        ValidatePackLimits(pack);
        DisposeDecodedBitmaps();
        DisposeSurface();
        ReturnReusableBuffer();
        _pack = pack;
    }

    private SKImage GetImage(AssetPack pack, string relativePath)
    {
        if (!AssetManifestContract.IsSafeRelativePath(relativePath))
        {
            throw new AssetManifestException($"Animation frame path is unsafe: {relativePath}");
        }

        if (!_fullPathCache.TryGetValue(relativePath, out var fullPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(pack.RootDirectory, relativePath));
            _fullPathCache.Add(relativePath, fullPath);
        }

        var cacheKey = fullPath;
        if (_imageCache.TryGetValue(cacheKey, out var cached))
        {
            TouchLru(cacheKey);
            return cached;
        }

        using var decoded = SKBitmap.Decode(fullPath)
            ?? throw new AssetManifestException($"Animation frame could not be decoded: {relativePath}");
        if (decoded.Width <= 0 || decoded.Height <= 0)
        {
            throw new AssetManifestException($"Animation frame has invalid dimensions: {relativePath}");
        }

        var decodedBytes = checked((long)decoded.RowBytes * decoded.Height);
        if (decodedBytes > MaxDecodedBitmapBytes)
        {
            throw new AssetManifestException(
                $"Asset pack exceeds decoded animation cache limits ({MaxDecodedBitmapCount} frames or {MaxDecodedBitmapBytes} bytes).");
        }

        // The frame decoded above isn't in the cache yet, so it can never be
        // picked as an eviction candidate here: only already-cached frames
        // (the least recently used first) make room for it.
        EvictUntilWithinLimits(decodedBytes);

        // The image owns its own copy (or ref) of the pixels, so the decoding
        // bitmap is released as soon as this returns.
        var image = SKImage.FromBitmap(decoded)
            ?? throw new AssetManifestException($"Animation frame could not be decoded: {relativePath}");
        _imageCache.Add(cacheKey, image);
        _imageCacheBytes.Add(cacheKey, decodedBytes);
        _imageCacheLruNodes.Add(cacheKey, _imageCacheLru.AddLast(cacheKey));
        _decodedBitmapBytes += decodedBytes;
        return image;
    }

    private void TouchLru(string cacheKey)
    {
        var node = _imageCacheLruNodes[cacheKey];
        if (!ReferenceEquals(node, _imageCacheLru.Last))
        {
            _imageCacheLru.Remove(node);
            _imageCacheLru.AddLast(node);
        }
    }

    private void EvictUntilWithinLimits(long incomingBytes)
    {
        while (_imageCacheLru.First is { } oldest
            && (_imageCache.Count >= MaxDecodedBitmapCount
                || incomingBytes > MaxDecodedBitmapBytes - _decodedBitmapBytes))
        {
            var key = oldest.Value;
            _imageCacheLru.RemoveFirst();
            _imageCacheLruNodes.Remove(key);
            if (_imageCache.Remove(key, out var evictedImage))
            {
                evictedImage.Dispose();
            }

            if (_imageCacheBytes.Remove(key, out var evictedBytes))
            {
                _decodedBitmapBytes -= evictedBytes;
            }
        }
    }

    private void EnsureSurface(int width, int height)
    {
        if (_surface is not null && _surface.Width == width && _surface.Height == height)
        {
            return;
        }

        DisposeSurface();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = new SKBitmap(info);
        _canvas = new SKCanvas(_surface);
        _paint = new SKPaint { IsAntialias = false };
    }

    private byte[] RentBuffer(int byteCount)
    {
        if (_reusableBuffer is not null && _reusableBuffer.Length >= byteCount)
        {
            var reusable = _reusableBuffer;
            _reusableBuffer = null;
            return reusable;
        }

        ReturnReusableBuffer();
        return ArrayPool<byte>.Shared.Rent(byteCount);
    }

    private void DisposeDecodedBitmaps()
    {
        foreach (var image in _imageCache.Values)
        {
            image.Dispose();
        }

        _imageCache.Clear();
        _imageCacheBytes.Clear();
        _imageCacheLru.Clear();
        _imageCacheLruNodes.Clear();
        _fullPathCache.Clear();
        _sourceCache.Clear();
        _decodedBitmapBytes = 0;
    }

    private void DisposeSurface()
    {
        _paint?.Dispose();
        _paint = null;
        _canvas?.Dispose();
        _canvas = null;
        _surface?.Dispose();
        _surface = null;
    }

    private void ReturnReusableBuffer()
    {
        if (_reusableBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_reusableBuffer);
            _reusableBuffer = null;
        }
    }

    private static (int Width, int Height) ValidateDimensions(PixelSize nominalSize, double scale)
    {
        if (nominalSize.Width <= 0 || nominalSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize), "Nominal dimensions must be positive.");
        }

        if (nominalSize.Width > MaxDimension || nominalSize.Height > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize), $"Nominal dimensions cannot exceed {MaxDimension} pixels.");
        }

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be finite and greater than zero.");
        }

        var width = checked((int)Math.Ceiling(nominalSize.Width * scale));
        var height = checked((int)Math.Ceiling(nominalSize.Height * scale));
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), $"Scaled dimensions must be between 1 and {MaxDimension} pixels.");
        }

        _ = checked(width * height * BytesPerPixel);
        return (width, height);
    }

    private static void ValidateCompositionInputs(
        AssetPack pack,
        AssetAnimation animation,
        AssetFrame frame,
        double scale,
        float opacity,
        TimeSpan semanticDuration,
        TimeSpan frameDuration)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(frame);
        if (!AssetManifestContract.IsSafeRelativePath(frame.File))
        {
            throw new AssetManifestException($"Animation frame path is unsafe: {frame.File}");
        }

        if (frame.DurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame duration must be positive.");
        }

        _ = ValidateDimensions(animation.NominalSize, scale);
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
    }

    private static void ValidatePackLimits(AssetPack pack)
    {
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long largestAnimationBytes = 0;
        string? largestAnimationLabel = null;

        foreach (var (outfitKey, outfit) in pack.Manifest.Outfits)
        {
            foreach (var (animationKey, animation) in outfit.Animations)
            {
                // Only this animation's own frames need to be resident together;
                // the composer never plays two animations at once, so the real
                // memory pressure is bounded by the biggest single animation, not
                // the sum of every animation in the pack (see below).
                var animationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var frame in animation.Frames)
                {
                    if (frame is not null)
                    {
                        uniquePaths.Add(frame.File);
                        animationPaths.Add(frame.File);
                    }
                }

                if (animation.ReducedMotion is not null)
                {
                    uniquePaths.Add(animation.ReducedMotion);
                    animationPaths.Add(animation.ReducedMotion);
                }

                long animationBytes = 0;
                foreach (var relativePath in animationPaths)
                {
                    if (!AssetManifestContract.IsSafeRelativePath(relativePath))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(Path.Combine(pack.RootDirectory, relativePath));
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var (width, height) = ReadPngDimensions(fullPath);
                    animationBytes = checked(animationBytes + (long)width * height * BytesPerPixel);
                }

                if (animationBytes > largestAnimationBytes)
                {
                    largestAnimationBytes = animationBytes;
                    largestAnimationLabel = $"{outfitKey}/{animationKey}";
                }
            }
        }

        if (uniquePaths.Count > MaxDecodedBitmapCount)
        {
            throw new AssetManifestException(
                $"Asset pack declares {uniquePaths.Count} decoded frames; the limit is {MaxDecodedBitmapCount}.");
        }

        if (largestAnimationBytes > MaxDecodedBitmapBytes)
        {
            throw new AssetManifestException(
                $"Asset pack animation '{largestAnimationLabel}' has a decoded working set of {largestAnimationBytes} bytes, "
                    + $"exceeding the {MaxDecodedBitmapBytes} byte limit.");
        }
    }

    /// <summary>
    /// Reads a PNG file's pixel dimensions straight out of its IHDR chunk
    /// (signature + length + "IHDR" + width + height, all fixed offsets)
    /// without decoding any pixel data. Used only to estimate decoded
    /// working-set size at admission time; actual composition still decodes
    /// through <see cref="SKBitmap.Decode(string)"/> and validates the real
    /// result.
    /// </summary>
    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        var bytesRead = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (bytesRead < header.Length
            || header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
        {
            throw new AssetManifestException($"File is not a valid PNG: {path}");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0)
        {
            throw new AssetManifestException($"PNG has invalid IHDR dimensions: {path}");
        }

        return (width, height);
    }

    private string GetSource(AssetPack pack, string relativePath)
    {
        if (_sourceCache.TryGetValue(relativePath, out var source))
        {
            return source;
        }

        source = $"{pack.Manifest.PackId}/{relativePath.Replace('\\', '/')}";
        _sourceCache.Add(relativePath, source);
        return source;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
