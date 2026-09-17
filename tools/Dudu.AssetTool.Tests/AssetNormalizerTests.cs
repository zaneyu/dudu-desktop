using Dudu.Core.Assets;
using Dudu.AssetTool;
using SkiaSharp;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class AssetNormalizerTests
{
    [Fact]
    public void Normalizer_keeps_a_shared_anchor_across_trimmed_frames()
    {
        var frames = new[]
        {
            new AssetInputFrame("first", PngFixture.Rectangle(8, 8, 1, 1, 3, 4), 100),
            new AssetInputFrame("second", PngFixture.Rectangle(8, 8, 3, 2, 2, 3), 100),
        };

        var result = AssetNormalizer.Normalize(frames, new PixelPoint(50, 100), 512);

        Assert.All(result.Frames, frame => Assert.Equal(result.Anchor, frame.Anchor));
        Assert.All(result.Frames, frame => Assert.Equal(512, frame.CanvasSize));
    }

    [Fact]
    public void Normalizer_does_not_upscale_and_emits_deterministic_premultiplied_png()
    {
        var input = PngFixture.Rectangle(600, 600, 0, 0, 600, 600);

        var first = AssetNormalizer.Normalize([new AssetInputFrame("large", input, 123)], new PixelPoint(50, 100), 512);
        var second = AssetNormalizer.Normalize([new AssetInputFrame("large", input, 123)], new PixelPoint(50, 100), 512);
        using var bitmap = SKBitmap.Decode(first.Frames[0].PngBytes);

        Assert.Equal(512, bitmap.Width);
        Assert.Equal(512, bitmap.Height);
        Assert.Equal(SKAlphaType.Premul, bitmap.AlphaType);
        Assert.Equal("premultiplied BGRA8888 PNG", first.Frames[0].ColorFormat);
        Assert.Equal(512d / 600d, first.Frames[0].Scale, precision: 6);
        Assert.Equal(first.Frames[0].Sha256, second.Frames[0].Sha256);
        Assert.Equal(first.Frames[0].PngBytes, second.Frames[0].PngBytes);
        Assert.Equal("frames/base/idle/0000-aaaaaaaaaaaa.png", AssetNormalizer.DeterministicFileName("idle", 0, new string('a', 64)));
    }

    [Fact]
    public void Decoder_removes_edge_connected_white_background_but_keeps_enclosed_white_art()
    {
        var png = PngFixture.WhiteBackgroundWithOutlinedWhiteDetail();

        var frames = AssetNormalizer.DecodeFrames(png, "white-background", 100);

        using var decoded = SKBitmap.Decode(frames[0].Bytes);
        Assert.Equal((byte)0, decoded.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, decoded.GetPixel(1, 1).Alpha);
        Assert.Equal(new SKColor(255, 255, 255, 255), decoded.GetPixel(3, 3));
    }

    private static class PngFixture
    {
        public static byte[] Rectangle(int width, int height, int x, int y, int rectangleWidth, int rectangleHeight)
        {
            var rgba = new byte[width * height * 4];
            for (var row = y; row < y + rectangleHeight; row++)
            {
                for (var column = x; column < x + rectangleWidth; column++)
                {
                    var index = (row * width + column) * 4;
                    rgba[index] = 255;
                    rgba[index + 1] = 128;
                    rgba[index + 2] = 32;
                    rgba[index + 3] = 255;
                }
            }

            return PngEncoder.Encode(width, height, rgba);
        }

        public static byte[] WhiteBackgroundWithOutlinedWhiteDetail()
        {
            using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(SKColors.White);
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(new SKRect(1, 1, 7, 7), paint);
                paint.Color = SKColors.White;
                canvas.DrawRect(new SKRect(2, 2, 6, 6), paint);
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data!.ToArray();
        }

        private static class PngEncoder
        {
            public static byte[] Encode(int width, int height, byte[] rgba)
            {
                using var stream = new MemoryStream();
                stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                using var chunk = new MemoryStream();
                WriteInt(chunk, width);
                WriteInt(chunk, height);
                chunk.WriteByte(8);
                chunk.WriteByte(6);
                chunk.WriteByte(0);
                chunk.WriteByte(0);
                chunk.WriteByte(0);
                WriteChunk(stream, "IHDR", chunk.ToArray());

                using var raw = new MemoryStream();
                for (var row = 0; row < height; row++)
                {
                    raw.WriteByte(0);
                    raw.Write(rgba, row * width * 4, width * 4);
                }

                WriteChunk(stream, "IDAT", Compress(raw.ToArray()));
                WriteChunk(stream, "IEND", []);
                return stream.ToArray();
            }

            private static byte[] Compress(byte[] bytes)
            {
                using var stream = new MemoryStream();
                using (var zlib = new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    zlib.Write(bytes);
                }

                return stream.ToArray();
            }

            private static void WriteChunk(Stream stream, string name, byte[] data)
            {
                WriteInt(stream, data.Length);
                var type = System.Text.Encoding.ASCII.GetBytes(name);
                stream.Write(type);
                stream.Write(data);
                WriteInt(stream, Crc32([.. type, .. data]));
            }

            private static void WriteInt(Stream stream, int value)
            {
                stream.WriteByte((byte)(value >> 24));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            private static int Crc32(byte[] bytes)
            {
                var crc = 0xFFFFFFFFu;
                foreach (var value in bytes)
                {
                    crc ^= value;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                    }
                }

                return unchecked((int)~crc);
            }
        }
    }
}
