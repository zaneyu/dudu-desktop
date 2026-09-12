namespace Dudu.AssetTool;

public static class MediaPayloadValidator
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsSupportedImage(byte[] bytes, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > maximumBytes)
        {
            return false;
        }

        return bytes.AsSpan().StartsWith(PngSignature)
            || bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })
            || bytes.AsSpan().StartsWith("GIF87a"u8)
            || bytes.AsSpan().StartsWith("GIF89a"u8)
            || (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8));
    }

    public static string ExtensionFor(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(PngSignature))
        {
            return ".png";
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
        {
            return ".jpg";
        }

        if (bytes.AsSpan().StartsWith("GIF"u8))
        {
            return ".gif";
        }

        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        throw new InvalidDataException("Unsupported image payload.");
    }
}
