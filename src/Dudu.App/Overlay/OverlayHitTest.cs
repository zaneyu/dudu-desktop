namespace Dudu.App.Overlay;

public static class OverlayHitTest
{
    /// <summary>
    /// Minimum premultiplied alpha that counts as an interactive pixel.
    /// Choice (pre-handoff menu-on-pet-click audit): 32 (~12.5% opacity)
    /// instead of the old 8. The composited pet frame carries a soft
    /// anti-aliased fringe/halo whose alpha sits in the 8..31 band several
    /// pixels outside the solid art; at 8 that halo armed drags and routed
    /// clicks to the bubble/menu path. At 32 the effective edge moves by
    /// less than one device pixel on solid art (the AA ramp crosses 32
    /// almost immediately) while the halo no longer arms. The window rect
    /// itself is intentionally NOT shrunk: it is exactly the nominal pet
    /// bounds, and shrinking it would clip real art on large pets.
    /// </summary>
    public const byte InteractiveAlphaThreshold = 32;

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
        try
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

            var sourceX = (int)Math.Floor(SaturateToDouble(windowX) / scale);
            var sourceY = (int)Math.Floor(SaturateToDouble(windowY) / scale);
            if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
            {
                return false;
            }

            var alphaOffset = (long)sourceY * stride + (long)sourceX * 4 + 3;
            return alphaOffset >= 0
                && alphaOffset < premultipliedBgra.Length
                && IsInteractive(premultipliedBgra[(int)alphaOffset]);
        }
        catch
        {
            // Hit testing must never throw: fail to non-interactive.
            return false;
        }
    }

    public static bool IsInteractive(
        ReadOnlySpan<byte> premultipliedBgra,
        int width,
        int height,
        int stride,
        int clientWidth,
        int clientHeight,
        int clientX,
        int clientY,
        IReadOnlyList<PixelRect>? explicitHitRegions = null)
    {
        try
        {
            if (Contains(explicitHitRegions, clientX, clientY))
            {
                return true;
            }

            if (!HasValidGeometry(width, height, stride)
                || clientWidth <= 0 || clientHeight <= 0
                || clientX < 0 || clientY < 0
                || clientX >= clientWidth || clientY >= clientHeight)
            {
                return false;
            }

            var sourceX = (int)((long)clientX * width / clientWidth);
            var sourceY = (int)((long)clientY * height / clientHeight);
            if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
            {
                return false;
            }

            var alphaOffset = (long)sourceY * stride + (long)sourceX * 4 + 3;
            return alphaOffset >= 0
                && alphaOffset < premultipliedBgra.Length
                && IsInteractive(premultipliedBgra[(int)alphaOffset]);
        }
        catch
        {
            return false;
        }
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
        try
        {
            if (!HasValidGeometry(width, height, stride)
                || !double.IsFinite(scale) || scale <= 0 || windowX < 0 || windowY < 0)
            {
                return 0;
            }

            var sourceX = (int)Math.Floor(SaturateToDouble(windowX) / scale);
            var sourceY = (int)Math.Floor(SaturateToDouble(windowY) / scale);
            if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
            {
                return 0;
            }

            var alphaOffset = (long)sourceY * stride + (long)sourceX * 4 + 3;
            return alphaOffset >= 0 && alphaOffset < premultipliedBgra.Length
                ? premultipliedBgra[(int)alphaOffset]
                : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    private static double SaturateToDouble(int value) => value;

    private static bool Contains(IReadOnlyList<PixelRect>? regions, int x, int y)
    {
        if (regions is null) return false;
        foreach (var region in regions)
        {
            try
            {
                if (region.Contains(x, y)) return true;
            }
            catch
            {
                // A malformed region never makes the point interactive.
            }
        }

        return false;
    }

    private static bool HasValidGeometry(int width, int height, int stride) =>
        width > 0 && height > 0 && width <= int.MaxValue / 4 && stride >= width * 4;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => SaturateToInt((long)X + Width);

    public int Bottom => SaturateToInt((long)Y + Height);

    public bool IsValid => Width > 0 && Height > 0;

    public bool Contains(int x, int y)
    {
        try
        {
            if (!IsValid) return false;
            return x >= X && x < Right && y >= Y && y < Bottom;
        }
        catch
        {
            return false;
        }
    }

    public bool Contains(PixelRect other)
    {
        try
        {
            if (!IsValid || !other.IsValid) return false;
            return other.X >= X && other.Y >= Y
                && other.Right <= Right && other.Bottom <= Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static int SaturateToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
}
