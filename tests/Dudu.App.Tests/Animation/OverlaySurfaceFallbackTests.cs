using Dudu.App.Animation;
using Dudu.App.Overlay;
using SkiaSharp;
using Xunit;

namespace Dudu.App.Tests.Animation;

/// <summary>
/// When a status surface has no valid bounds, the fallback paint must stay a
/// small strip: the pet frame underneath must not be covered.
/// </summary>
public sealed class OverlaySurfaceFallbackTests
{
    [Fact]
    public void Invalid_bounds_paints_strip_not_full_canvas()
    {
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var snapshot = new OverlaySurfaceSnapshot(
            OverlayActionSurfaceKind.Status,
            null,
            [],
            new ComfortPanelState(false, false, BreathVisualPhase.Idle, string.Empty),
            false,
            "stuck on gray screen")
        {
            DetailRegion = null,
        };

        OverlaySurfaceRenderer.Draw(canvas, snapshot);
        canvas.Flush();

        // Pet area stays transparent: nothing painted over the top rows.
        Assert.Equal(0, bitmap.GetPixel(10, 10).Alpha);
        Assert.Equal(0, bitmap.GetPixel(100, 50).Alpha);
        Assert.Equal(0, bitmap.GetPixel(190, 150).Alpha);

        // The diagnostic strip at the bottom is still painted.
        var stripPixels = 0;
        for (var x = 0; x < 200; x += 4)
        {
            if (bitmap.GetPixel(x, 195).Alpha != 0)
            {
                stripPixels++;
            }
        }

        Assert.True(stripPixels > 0, "Expected the fallback strip to paint the error.");
    }

    [Fact]
    public void Valid_bounds_still_paint_the_arranged_panel()
    {
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var snapshot = new OverlaySurfaceSnapshot(
            OverlayActionSurfaceKind.Status,
            new PixelRect(10, 10, 100, 40),
            [],
            new ComfortPanelState(false, false, BreathVisualPhase.Idle, string.Empty),
            false,
            "stuck on gray screen")
        {
            DetailRegion = new PixelRect(14, 14, 92, 32),
        };

        OverlaySurfaceRenderer.Draw(canvas, snapshot);
        canvas.Flush();

        // Inside the arranged panel something painted; far outside it did not.
        Assert.NotEqual(0, bitmap.GetPixel(50, 30).Alpha);
        Assert.Equal(0, bitmap.GetPixel(190, 190).Alpha);
    }
}
