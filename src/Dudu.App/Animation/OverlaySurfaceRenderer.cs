using Dudu.App.Overlay;
using SkiaSharp;

namespace Dudu.App.Animation;

/// <summary>Draws action buttons into the same client-pixel frame used for
/// no-activate hit testing. Painted action rectangles exactly match the
/// controller's hit regions.</summary>
internal static class OverlaySurfaceRenderer
{
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private static readonly SKFont LabelFont = new(Typeface, 14);
    private static readonly SKFont DetailFont = new(Typeface, 11);

    public static void Draw(SKCanvas canvas, OverlaySurfaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Kind == OverlayActionSurfaceKind.Closed
            || snapshot.Bounds is not { } bounds
            || !bounds.IsValid
            || snapshot.Actions.Count == 0)
        {
            return;
        }

        var surface = ToRect(bounds);
        canvas.Save();
        canvas.ClipRect(surface);
        using var surfacePaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 250, 246, 248),
            Style = SKPaintStyle.Fill,
        };
        using var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(210, 194, 211, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        using var actionPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 255),
            Style = SKPaintStyle.Fill,
        };
        using var actionBorderPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(228, 214, 225, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(46, 40, 48, 255),
            Style = SKPaintStyle.Fill,
        };
        using var detailPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(94, 82, 96, 255),
            Style = SKPaintStyle.Fill,
        };
        using var errorPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(170, 58, 55, 255),
            Style = SKPaintStyle.Fill,
        };

        canvas.DrawRoundRect(surface, 12, 12, surfacePaint);
        canvas.DrawRoundRect(surface, 12, 12, borderPaint);
        foreach (var action in snapshot.Actions)
        {
            var region = ToRect(action.HitRegion);
            canvas.DrawRoundRect(region, 8, 8, actionPaint);
            canvas.DrawRoundRect(region, 8, 8, actionBorderPaint);
            DrawCenteredLabel(canvas, action.Label, region, LabelFont, textPaint);
        }

        if (snapshot.Kind == OverlayActionSurfaceKind.Comfort)
        {
            var detail = snapshot.IsReducedMotion
                ? "Reduced motion"
                : snapshot.ComfortPanel.IsBreathing
                    ? $"{snapshot.ComfortPanel.Phase}: {snapshot.ComfortPanel.Instruction}"
                    : snapshot.ComfortPanel.Instruction;
            canvas.DrawText(detail, surface.Left + 12, surface.Top + 10, SKTextAlign.Left, DetailFont, detailPaint);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            canvas.DrawText(snapshot.ErrorMessage, surface.Left + 12, surface.Bottom - 6, SKTextAlign.Left, DetailFont, errorPaint);
        }

        canvas.Restore();
    }

    private static void DrawCenteredLabel(
        SKCanvas canvas,
        string text,
        SKRect region,
        SKFont font,
        SKPaint paint)
    {
        font.GetFontMetrics(out var metrics);
        var baseline = region.MidY - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, region.Left + 12, baseline, SKTextAlign.Left, font, paint);
    }

    private static SKRect ToRect(PixelRect rectangle) =>
        SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
