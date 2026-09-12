namespace Dudu.App.Overlay;

public static class OverlayHitTest
{
    public const byte InteractiveAlphaThreshold = 8;

    public static bool IsInteractive(byte alpha) => alpha >= InteractiveAlphaThreshold;

    public static bool IsInteractive(
        ReadOnlySpan<byte> premultipliedBgra,
        int width,
        int height,
        int stride,
        double scale,
        int windowX,
        int windowY,
        IReadOnlyList<PixelRect>? explicitHitRegions = null)
    {
        if (Contains(explicitHitRegions, windowX, windowY))
        {
            return true;
        }

        if (!HasValidGeometry(width, height, stride)
            || !double.IsFinite(scale) || scale <= 0
            || windowX < 0 || windowY < 0)
        {
            return false;
        }

        var sourceX = (int)Math.Floor(windowX / scale);
        var sourceY = (int)Math.Floor(windowY / scale);
        if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
        {
            return false;
        }

        var alphaOffset = checked(sourceY * stride + sourceX * 4 + 3);
        return alphaOffset < premultipliedBgra.Length
            && IsInteractive(premultipliedBgra[alphaOffset]);
    }

    public static byte AlphaAt(
        ReadOnlySpan<byte> premultipliedBgra,
        int width,
        int height,
        int stride,
        double scale,
        int windowX,
        int windowY)
    {
        if (!HasValidGeometry(width, height, stride)
            || !double.IsFinite(scale) || scale <= 0 || windowX < 0 || windowY < 0)
        {
            return 0;
        }

        var sourceX = (int)Math.Floor(windowX / scale);
        var sourceY = (int)Math.Floor(windowY / scale);
        if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
        {
            return 0;
        }

        var alphaOffset = checked(sourceY * stride + sourceX * 4 + 3);
        return alphaOffset < premultipliedBgra.Length ? premultipliedBgra[alphaOffset] : (byte)0;
    }

    private static bool Contains(IReadOnlyList<PixelRect>? regions, int x, int y) =>
        regions is not null && regions.Any(region => region.Contains(x, y));

    private static bool HasValidGeometry(int width, int height, int stride) =>
        width > 0 && height > 0 && width <= int.MaxValue / 4 && stride >= width * 4;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool IsValid => Width > 0 && Height > 0;

    public bool Contains(int x, int y) =>
        IsValid && x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(PixelRect other) =>
        IsValid && other.IsValid
        && other.X >= X && other.Y >= Y
        && other.Right <= Right && other.Bottom <= Bottom;
}
