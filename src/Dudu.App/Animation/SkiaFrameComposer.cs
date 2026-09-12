using System.Buffers;
using System.Runtime.InteropServices;
using Dudu.Core.Assets;
using SkiaSharp;

namespace Dudu.App.Animation;

public sealed class SkiaFrameComposer : IDisposable, IFrameBufferReleaser
{
    private const int BytesPerPixel = 4;
    private const int MaxDimension = 4096;

    private readonly object _gate = new();
    private static readonly SKSamplingOptions SamplingOptions = new(SKFilterMode.Nearest);
    private readonly Dictionary<string, SKBitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fullPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceCache = new(StringComparer.Ordinal);
    private SKBitmap? _surface;
    private SKCanvas? _canvas;
    private SKPaint? _paint;
    private byte[]? _reusableBuffer;
    private AssetPack? _pack;
    private bool _disposed;

    public SkiaFrameComposer()
    {
    }

    public SkiaFrameComposer(AssetPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
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

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsurePack(pack);
            var dimensions = ValidateDimensions(animation.NominalSize, scale);
            var source = GetSource(pack, frame.File);
            var bitmap = GetBitmap(pack, frame.File);
            EnsureSurface(dimensions.Width, dimensions.Height);
            var output = _surface!;

            _canvas!.Clear(SKColors.Transparent);
            _paint!.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * byte.MaxValue));
            var destination = new SKRect(0, 0, dimensions.Width, dimensions.Height);
            _canvas.DrawBitmap(bitmap, destination, SamplingOptions, _paint);

            var stride = output.RowBytes;
            var byteCount = checked(stride * dimensions.Height);
            var buffer = RentBuffer(byteCount);
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
                this);
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
            DisposeDecodedBitmaps();
            DisposeSurface();
            ReturnReusableBuffer();
            _pack = null;
        }
    }

    void IFrameBufferReleaser.Release(byte[] buffer) => Release(buffer);

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

        DisposeDecodedBitmaps();
        DisposeSurface();
        ReturnReusableBuffer();
        _pack = pack;
    }

    private SKBitmap GetBitmap(AssetPack pack, string relativePath)
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
        if (_bitmapCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var decoded = SKBitmap.Decode(fullPath)
            ?? throw new AssetManifestException($"Animation frame could not be decoded: {relativePath}");
        if (decoded.Width <= 0 || decoded.Height <= 0)
        {
            decoded.Dispose();
            throw new AssetManifestException($"Animation frame has invalid dimensions: {relativePath}");
        }

        _bitmapCache.Add(cacheKey, decoded);
        return decoded;
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
        foreach (var bitmap in _bitmapCache.Values)
        {
            bitmap.Dispose();
        }

        _bitmapCache.Clear();
        _fullPathCache.Clear();
        _sourceCache.Clear();
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
