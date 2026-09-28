using System.Runtime.InteropServices;
using Dudu.Core.Models;
using SkiaSharp;

namespace Dudu.App.Animation;

/// <summary>Overlay rendering helpers shared by the pet canvas: the Windows
/// high-contrast reading that picks the overlay palette. (The pointer-only
/// action bubble it used to paint was removed; clicking Dudu just pets.)</summary>
internal static class OverlaySurfaceRenderer
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    public static bool IsHighContrastEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var highContrast = new HighContrast
        {
            Size = (uint)Marshal.SizeOf<HighContrast>(),
        };
        return SystemParametersInfoW(SpiGetHighContrast, highContrast.Size, ref highContrast, 0)
            && (highContrast.Flags & HcfHighContrastOn) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint action,
        uint parameter,
        ref HighContrast value,
        uint winIni);
}

/// <summary>Theme and high-contrast aware colors for the no-activate surface.
/// These mirror the app palette without hard-coding a light-only overlay.</summary>
public sealed record OverlaySurfacePalette(
    SKColor Surface,
    SKColor Border,
    SKColor ActionSurface,
    SKColor ActionBorder,
    SKColor Text,
    SKColor DetailText,
    SKColor ErrorText)
{
    public static OverlaySurfacePalette For(AppTheme theme, bool highContrast) => highContrast
        ? new OverlaySurfacePalette(SKColors.Black, SKColors.White, SKColors.Black, SKColors.Yellow, SKColors.White, SKColors.White, SKColors.Yellow)
        : theme == AppTheme.Dark
            ? new OverlaySurfacePalette(
                new SKColor(42, 38, 48, 248), new SKColor(206, 190, 224),
                new SKColor(61, 55, 70), new SKColor(225, 208, 238),
                new SKColor(250, 247, 252), new SKColor(220, 213, 229), new SKColor(255, 183, 178))
            : new OverlaySurfacePalette(
                new SKColor(255, 250, 246, 248), new SKColor(210, 194, 211),
                SKColors.White, new SKColor(228, 214, 225),
                new SKColor(46, 40, 48), new SKColor(94, 82, 96), new SKColor(170, 58, 55));
}

/// <summary>One rendered UK partner-clock pill and where it sits on the pet
/// canvas. Rendered once per label/theme/canvas size and then only blitted.</summary>
internal sealed record PartnerClockPill(SKImage Image, int X, int Y) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>Paints the small "UK 14:05" pill that floats above Dudu. It uses
/// the overlay palette, so it follows the app theme and high contrast, and it
/// is only ever placed fully inside the canvas so it can never be clipped.</summary>
internal static class PartnerClockPillRenderer
{
    public const int MinimumCanvasSize = 96;
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

    public static PartnerClockPill? Render(
        string label,
        bool isNight,
        OverlaySurfacePalette palette,
        int canvasWidth,
        int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (string.IsNullOrWhiteSpace(label)
            || canvasWidth < MinimumCanvasSize
            || canvasHeight < MinimumCanvasSize)
        {
            return null;
        }

        var fontSize = Math.Clamp(canvasHeight * 0.042f, 11f, 28f);
        using var font = new SKFont(Typeface, fontSize) { Edging = SKFontEdging.Antialias };
        using var textPaint = new SKPaint { IsAntialias = true, Color = palette.Text, Style = SKPaintStyle.Fill };
        var textWidth = font.MeasureText(label, textPaint);
        var iconDiameter = fontSize * 0.9f;
        var padding = fontSize * 0.7f;
        var gap = fontSize * 0.4f;
        var width = (int)Math.Ceiling(padding + iconDiameter + gap + textWidth + padding);
        var height = (int)Math.Ceiling(fontSize * 1.8f);
        if (width > canvasWidth - 4 || height > canvasHeight / 4)
        {
            return null;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var radius = height / 2f;
            var body = new SKRect(0.5f, 0.5f, width - 0.5f, height - 0.5f);
            using var fill = new SKPaint { IsAntialias = true, Color = palette.Surface, Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { IsAntialias = true, Color = palette.Border, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRoundRect(body, radius, radius, fill);
            canvas.DrawRoundRect(body, radius, radius, stroke);

            var iconCenter = new SKPoint(padding + iconDiameter / 2f, height / 2f);
            var iconRadius = iconDiameter / 2f;
            using var iconPaint = new SKPaint { IsAntialias = true, Color = palette.DetailText, Style = SKPaintStyle.Fill };
            if (isNight)
            {
                // Crescent: a disc with an offset surface-coloured disc bitten out.
                canvas.DrawCircle(iconCenter, iconRadius, iconPaint);
                canvas.DrawCircle(iconCenter.X + iconRadius * 0.45f, iconCenter.Y - iconRadius * 0.3f, iconRadius * 0.85f, fill);
            }
            else
            {
                canvas.DrawCircle(iconCenter, iconRadius * 0.55f, iconPaint);
                using var rayPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = palette.DetailText,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, fontSize * 0.08f),
                    StrokeCap = SKStrokeCap.Round,
                };
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * MathF.PI / 4f;
                    var (sin, cos) = MathF.SinCos(angle);
                    canvas.DrawLine(
                        iconCenter.X + cos * iconRadius * 0.75f, iconCenter.Y + sin * iconRadius * 0.75f,
                        iconCenter.X + cos * iconRadius, iconCenter.Y + sin * iconRadius,
                        rayPaint);
                }
            }

            font.GetFontMetrics(out var metrics);
            var baseline = height / 2f - (metrics.Ascent + metrics.Descent) / 2f;
            canvas.DrawText(label, padding + iconDiameter + gap, baseline, SKTextAlign.Left, font, textPaint);
        }

        var image = SKImage.FromBitmap(bitmap);
        var x = (canvasWidth - width) / 2;
        var y = Math.Max(2, (int)Math.Round(canvasHeight * 0.02));
        return new PartnerClockPill(image, x, y);
    }
}
