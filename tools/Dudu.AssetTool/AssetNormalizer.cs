using System.Security.Cryptography;
using Dudu.Core.Assets;
using SkiaSharp;

namespace Dudu.AssetTool;

public sealed record AssetInputFrame(string Name, byte[] Bytes, int DurationMs);

public sealed record NormalizedAssetFrame(
    string Name,
    byte[] PngBytes,
    string Sha256,
    int DurationMs,
    PixelPoint Anchor,
    int CanvasSize,
    PixelSize TrimmedSize,
    double Scale,
    string ColorFormat);

public sealed class NormalizedAssetSet
{
    public NormalizedAssetSet(IReadOnlyList<NormalizedAssetFrame> frames, PixelPoint anchor, int canvasSize)
    {
        Frames = frames;
        Anchor = anchor;
        CanvasSize = canvasSize;
    }

    public IReadOnlyList<NormalizedAssetFrame> Frames { get; }

    public PixelPoint Anchor { get; }

    public int CanvasSize { get; }
}

public static class AssetNormalizer
{
    public static IReadOnlyList<AssetInputFrame> DecodeFrames(
        byte[] bytes,
        string name,
        int staticDurationMs)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (staticDurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(staticDurationMs));
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException($"Source '{name}' is not a decodable image.");
        // Static PNG/JPEG codecs may report zero animation frames even though
        // frame zero is decodable. Treat those sources as one frame so they go
        // through the same background cleanup as GIF sources.
        var frameCount = Math.Max(1, codec.FrameCount);
        var info = codec.Info;
        var frameInfo = codec.FrameInfo;
        var frames = new List<AssetInputFrame>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(
                bitmap.Info,
                bitmap.GetPixels(),
                bitmap.RowBytes,
                new SKCodecOptions(index));
            if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            {
                throw new InvalidDataException($"Could not decode GIF frame {index} from '{name}': {result}.");
            }

            var duration = frameInfo.Length > index ? Math.Max(1, frameInfo[index].Duration) : staticDurationMs;
            RemoveEdgeConnectedLightBackground(bitmap);
            frames.Add(new AssetInputFrame(name + $"-{index:D4}", EncodeBitmap(bitmap), duration));
        }

        return frames;
    }

    public static NormalizedAssetSet Normalize(
        IEnumerable<AssetInputFrame> frames,
        PixelPoint packAnchor,
        int maxCanvasSize)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (maxCanvasSize is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCanvasSize), "The normalized canvas must be between 1 and 512 pixels.");
        }

        if (packAnchor.X < 0 || packAnchor.Y < 0 || packAnchor.X >= maxCanvasSize || packAnchor.Y >= maxCanvasSize)
        {
            throw new ArgumentOutOfRangeException(nameof(packAnchor), "The shared pack anchor must be inside the normalized canvas.");
        }

        var inputs = frames.ToArray();
        if (inputs.Length == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        }

        var normalized = new List<NormalizedAssetFrame>(inputs.Length);
        foreach (var input in inputs)
        {
            if (input.DurationMs <= 0)
            {
                throw new ArgumentException($"Frame '{input.Name}' has a non-positive duration.", nameof(frames));
            }

            using var source = Decode(input.Bytes, input.Name);
            var bounds = FindOpaqueBounds(source);
            var trimmedWidth = bounds?.Width ?? 0;
            var trimmedHeight = bounds?.Height ?? 0;
            var scale = bounds is null
                ? 1d
                : Math.Min(1d, Math.Min((double)maxCanvasSize / trimmedWidth, (double)maxCanvasSize / trimmedHeight));
            var outputWidth = Math.Max(1, (int)Math.Round(trimmedWidth * scale, MidpointRounding.ToEven));
            var outputHeight = Math.Max(1, (int)Math.Round(trimmedHeight * scale, MidpointRounding.ToEven));

            using var output = new SKBitmap(new SKImageInfo(maxCanvasSize, maxCanvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
            output.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(output))
            {
                var left = Math.Clamp(packAnchor.X - outputWidth / 2, 0, maxCanvasSize - outputWidth);
                var top = Math.Clamp(packAnchor.Y - outputHeight / 2, 0, maxCanvasSize - outputHeight);
                var destination = new SKRect(left, top, left + outputWidth, top + outputHeight);
                if (bounds is not null)
                {
                    var sourceRect = new SKRect(bounds.Value.Left, bounds.Value.Top, bounds.Value.Right, bounds.Value.Bottom);
                    canvas.DrawBitmap(source, sourceRect, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(output);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException($"Skia could not encode frame '{input.Name}'.");
            var png = data.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            normalized.Add(new NormalizedAssetFrame(
                input.Name,
                png,
                hash,
                input.DurationMs,
                packAnchor,
                maxCanvasSize,
                new PixelSize(trimmedWidth, trimmedHeight),
                scale,
                "premultiplied BGRA8888 PNG"));
        }

        return new NormalizedAssetSet(normalized, packAnchor, maxCanvasSize);
    }

    public static byte[] CreateNeutralBearPng(int size = 128)
    {
        if (size is < 16 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, size / 32f) })
        {
            var center = size / 2f;
            var brown = new SKColor(150, 105, 72, 255);
            var dark = new SKColor(54, 38, 32, 255);
            var cream = new SKColor(225, 190, 146, 255);
            fill.Color = brown;
            canvas.DrawCircle(center - size * .22f, size * .28f, size * .15f, fill);
            canvas.DrawCircle(center + size * .22f, size * .28f, size * .15f, fill);
            canvas.DrawRoundRect(new SKRect(size * .2f, size * .22f, size * .8f, size * .86f), size * .24f, size * .24f, fill);
            fill.Color = cream;
            canvas.DrawOval(new SKRect(size * .31f, size * .53f, size * .69f, size * .78f), fill);
            fill.Color = dark;
            canvas.DrawCircle(size * .4f, size * .48f, size * .035f, fill);
            canvas.DrawCircle(size * .6f, size * .48f, size * .035f, fill);
            canvas.DrawOval(new SKRect(size * .455f, size * .59f, size * .545f, size * .67f), fill);
            stroke.Color = dark;
            canvas.DrawArc(new SKRect(size * .43f, size * .62f, size * .57f, size * .74f), 10, 160, false, stroke);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Skia could not encode the fallback bear.");
        return data.ToArray();
    }

    public static string DeterministicFileName(string animationKey, int frameIndex, string sha256)
    {
        if (!AssetManifestContract.IsSafeKey(animationKey) || frameIndex < 0 || sha256.Length < 12)
        {
            throw new ArgumentException("Unsafe animation key or frame hash.");
        }

        return $"frames/base/{animationKey}/{frameIndex:D4}-{sha256[..12].ToLowerInvariant()}.png";
    }

    private static SKBitmap Decode(byte[] bytes, string name)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SKBitmap.Decode(stream)
            ?? throw new InvalidDataException($"Frame '{name}' is not a decodable image.");
    }

    private static byte[] EncodeBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Skia could not encode a decoded image frame.");
        return data.ToArray();
    }

    /// <summary>
    /// Imported sticker/GIF sources commonly arrive with a solid white page
    /// behind the artwork. The desktop pet is rendered into a transparent
    /// layered window, so keeping that page would produce a white square around
    /// every frame. Remove only near-white pixels connected to the image edge;
    /// enclosed white details, such as Bubu's body or highlights, remain intact.
    /// </summary>
    private static void RemoveEdgeConnectedLightBackground(SKBitmap bitmap)
    {
        var visited = new bool[checked(bitmap.Width * bitmap.Height)];
        var pending = new Queue<int>();

        for (var x = 0; x < bitmap.Width; x++)
        {
            EnqueueIfCandidate(x, 0);
            EnqueueIfCandidate(x, bitmap.Height - 1);
        }

        for (var y = 1; y < bitmap.Height - 1; y++)
        {
            EnqueueIfCandidate(0, y);
            EnqueueIfCandidate(bitmap.Width - 1, y);
        }

        while (pending.Count > 0)
        {
            var index = pending.Dequeue();
            var x = index % bitmap.Width;
            var y = index / bitmap.Width;
            if (!IsLightBackground(bitmap.GetPixel(x, y)))
            {
                continue;
            }

            bitmap.SetPixel(x, y, SKColors.Transparent);
            EnqueueIfCandidate(x - 1, y);
            EnqueueIfCandidate(x + 1, y);
            EnqueueIfCandidate(x, y - 1);
            EnqueueIfCandidate(x, y + 1);
        }

        void EnqueueIfCandidate(int x, int y)
        {
            if ((uint)x >= (uint)bitmap.Width || (uint)y >= (uint)bitmap.Height)
            {
                return;
            }

            var index = checked(y * bitmap.Width + x);
            if (visited[index] || !IsLightBackground(bitmap.GetPixel(x, y)))
            {
                return;
            }

            visited[index] = true;
            pending.Enqueue(index);
        }
    }

    private static bool IsLightBackground(SKColor color)
    {
        if (color.Alpha == 0 || color.Red < 235 || color.Green < 235 || color.Blue < 235)
        {
            return false;
        }

        var spread = Math.Max(color.Red, Math.Max(color.Green, color.Blue))
            - Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        return spread <= 24;
    }

    private static SKRectI? FindOpaqueBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = 0;
        var bottom = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        return right == 0 ? null : new SKRectI(left, top, right, bottom);
    }
}
