using Dudu.AssetTool;
using SkiaSharp;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class GifExtractionTests
{
    [Fact]
    public void Gif_extraction_preserves_order_delays_and_disposal_compositing()
    {
        var gif = GifFixture.ThreeFramesWithRestoreBackground;

        var frames = AssetNormalizer.DecodeFrames(gif, "fixture", 100);

        Assert.Equal(3, frames.Count);
        Assert.Equal([20, 30, 40], frames.Select(frame => frame.DurationMs));
        using var decodedFinal = SKBitmap.Decode(frames[2].Bytes);
        Assert.Equal(new SKColor(255, 0, 0, 255), decodedFinal.GetPixel(0, 0));
        Assert.Equal((byte)0, decodedFinal.GetPixel(1, 0).Alpha);
        Assert.Equal(new SKColor(0, 255, 0, 255), decodedFinal.GetPixel(2, 0));
    }

    private static class GifFixture
    {
        public static byte[] ThreeFramesWithRestoreBackground => Build();

        private static byte[] Build()
        {
            using var stream = new MemoryStream();
            WriteAscii(stream, "GIF89a");
            WriteU16(stream, 3);
            WriteU16(stream, 1);
            stream.WriteByte(0x81); // global table, 4 entries
            stream.WriteByte(3); // transparent background index
            stream.WriteByte(0);
            stream.Write([255, 0, 0, 0, 0, 255, 0, 255, 0, 0, 0, 0]);

            WriteGraphicControlExtension(stream, disposal: 1, delayHundredths: 2);
            WriteImage(stream, x: 0, colorIndex: 0);
            WriteGraphicControlExtension(stream, disposal: 2, delayHundredths: 3);
            WriteImage(stream, x: 1, colorIndex: 1);
            WriteGraphicControlExtension(stream, disposal: 1, delayHundredths: 4);
            WriteImage(stream, x: 2, colorIndex: 2);
            stream.WriteByte(0x3B);
            return stream.ToArray();
        }

        private static void WriteGraphicControlExtension(Stream stream, int disposal, int delayHundredths)
        {
            stream.Write([0x21, 0xF9, 0x04, (byte)(disposal << 2)]);
            WriteU16(stream, delayHundredths);
            stream.Write([3, 0]);
        }

        private static void WriteImage(Stream stream, int x, int colorIndex)
        {
            stream.WriteByte(0x2C);
            WriteU16(stream, x);
            WriteU16(stream, 0);
            WriteU16(stream, 1);
            WriteU16(stream, 1);
            stream.WriteByte(0);
            stream.WriteByte(2);
            var data = LzwSinglePixel(colorIndex);
            stream.WriteByte((byte)data.Length);
            stream.Write(data);
            stream.WriteByte(0);
        }

        private static byte[] LzwSinglePixel(int colorIndex)
        {
            var bits = new List<int>();
            WriteCode(bits, 4, 3);
            WriteCode(bits, colorIndex, 3);
            WriteCode(bits, 5, 3);
            var result = new byte[(bits.Count + 7) / 8];
            for (var index = 0; index < bits.Count; index++)
            {
                result[index / 8] |= (byte)(bits[index] << (index % 8));
            }

            return result;
        }

        private static void WriteCode(ICollection<int> bits, int value, int bitCount)
        {
            for (var bit = 0; bit < bitCount; bit++)
            {
                bits.Add((value >> bit) & 1);
            }
        }

        private static void WriteU16(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteAscii(Stream stream, string value) => stream.Write(System.Text.Encoding.ASCII.GetBytes(value));
    }
}
