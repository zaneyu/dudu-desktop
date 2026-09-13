using System.Runtime.InteropServices;
using Dudu.App.Overlay;
using Dudu.Core.Models;
using SkiaSharp;

namespace Dudu.App.Animation;

/// <summary>Paints the native overlay actions into the same premultiplied frame
/// used for hit testing.  Geometry comes exclusively from the controller, so
/// painted and clickable regions cannot drift apart.</summary>
internal static class OverlaySurfaceRenderer
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private static readonly SKFont LabelFont = new(Typeface, 14);
    private static readonly SKFont DetailFont = new(Typeface, 11);

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

    public static void Draw(
        SKCanvas canvas,
        OverlaySurfaceSnapshot snapshot,
        OverlaySurfacePalette? requestedPalette = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Kind == OverlayActionSurfaceKind.Closed)
        {
            return;
        }

        // A status/error surface is still useful when the work area was too
        // small to arrange buttons. Fall back to the current canvas clip so
        // the failure is painted instead of silently disappearing.
        var surface = snapshot.Bounds is { } bounds && bounds.IsValid
            ? ToRect(bounds)
            : canvas.LocalClipBounds;
        if (surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var palette = requestedPalette
            ?? OverlaySurfacePalette.For(snapshot.Theme, snapshot.IsHighContrast || IsHighContrastEnabled());
        canvas.Save();
        try
        {
            canvas.ClipRect(surface);
            using var surfacePaint = Fill(palette.Surface);
            using var borderPaint = Stroke(palette.Border);
            using var actionPaint = Fill(palette.ActionSurface);
            using var actionBorderPaint = Stroke(palette.ActionBorder);
            using var textPaint = Fill(palette.Text);
            using var detailPaint = Fill(palette.DetailText);
            using var errorPaint = Fill(palette.ErrorText);

            canvas.DrawRoundRect(surface, 12, 12, surfacePaint);
            canvas.DrawRoundRect(surface, 12, 12, borderPaint);
            foreach (var action in snapshot.Actions)
            {
                var region = ToRect(action.HitRegion);
                canvas.DrawRoundRect(region, 8, 8, actionPaint);
                canvas.DrawRoundRect(region, 8, 8, actionBorderPaint);
                DrawCenteredLabel(canvas, action.Label, region, LabelFont, textPaint);
            }

            if (snapshot.Kind == OverlayActionSurfaceKind.Comfort
                && string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                && !string.IsNullOrWhiteSpace(snapshot.ComfortPanel.Instruction))
            {
                // Reduced motion has one stable, visible instruction rather
                // than a breath phase that changes without an animation.
                var detail = snapshot.IsReducedMotion
                    ? $"Reduced motion: {snapshot.ComfortPanel.Instruction}"
                    : snapshot.ComfortPanel.IsBreathing
                        ? $"{snapshot.ComfortPanel.Phase}: {snapshot.ComfortPanel.Instruction}"
                        : snapshot.ComfortPanel.Instruction;
                DrawDetail(canvas, detail, snapshot.DetailRegion, detailPaint);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
            {
                // Status surfaces intentionally render without action regions;
                // the matching Settings/Home route carries the accessible live
                // error text as well.
                DrawDetail(canvas, snapshot.ErrorMessage, snapshot.DetailRegion, errorPaint);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static void DrawDetail(SKCanvas canvas, string text, PixelRect? requestedRegion, SKPaint paint)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (requestedRegion is not { } pixelRegion || !pixelRegion.IsValid) return;
        var region = ToRect(pixelRegion);
        canvas.Save();
        try
        {
            canvas.ClipRect(region);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = string.Empty;
            var baseline = region.Top + DetailFont.Size;
            var lineHeight = DetailFont.Size + 3;
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && DetailFont.MeasureText(candidate, paint) > region.Width)
                {
                    canvas.DrawText(line, region.Left, baseline, SKTextAlign.Left, DetailFont, paint);
                    baseline += lineHeight;
                    if (baseline > region.Bottom) return;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            if (line.Length > 0 && baseline <= region.Bottom)
            {
                canvas.DrawText(line, region.Left, baseline, SKTextAlign.Left, DetailFont, paint);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static void DrawCenteredLabel(SKCanvas canvas, string text, SKRect region, SKFont font, SKPaint paint)
    {
        font.GetFontMetrics(out var metrics);
        var baseline = region.MidY - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, region.MidX, baseline, SKTextAlign.Center, font, paint);
    }

    private static SKPaint Fill(SKColor color) => new()
    {
        IsAntialias = true,
        Color = color,
        Style = SKPaintStyle.Fill,
    };

    private static SKPaint Stroke(SKColor color) => new()
    {
        IsAntialias = true,
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1,
    };

    private static SKRect ToRect(PixelRect rectangle) =>
        SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

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

/// <summary>Immutable information for one painted action.  Automation ID and
/// settings destination document its keyboard/tray equivalent.</summary>
public sealed record OverlaySurfaceAction(string Label, PixelRect HitRegion)
{
    public string AutomationId { get; init; } = string.Empty;
    public string SettingsDestination { get; init; } = string.Empty;

    public OverlaySurfaceAction(
        string label,
        string automationId,
        PixelRect hitRegion,
        string settingsDestination = "")
        : this(label, hitRegion)
    {
        AutomationId = automationId ?? string.Empty;
        SettingsDestination = settingsDestination ?? string.Empty;
    }
}

public sealed record OverlaySurfaceSnapshot(
    OverlayActionSurfaceKind Kind,
    PixelRect? Bounds,
    IReadOnlyList<OverlaySurfaceAction> Actions,
    ComfortPanelState ComfortPanel,
    bool IsReducedMotion,
    string? ErrorMessage)
{
    public PixelRect? DetailRegion { get; init; }
    public long GeometryVersion { get; init; }
    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool IsHighContrast { get; init; }

    public OverlaySurfaceSnapshot(
        OverlayActionSurfaceKind kind,
        PixelRect? bounds,
        IReadOnlyList<OverlaySurfaceAction> actions,
        ComfortPanelState comfortPanel,
        bool isReducedMotion,
        string? errorMessage,
        AppTheme theme,
        bool isHighContrast)
        : this(kind, bounds, actions, comfortPanel, isReducedMotion, errorMessage)
    {
        Theme = theme;
        IsHighContrast = isHighContrast;
    }
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
