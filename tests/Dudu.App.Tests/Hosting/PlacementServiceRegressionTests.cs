using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// DPI-factor math and unplug clamping for the placement service: the window
/// must follow the suggested-RECT scale and never strand on a stale rect.
/// </summary>
public sealed class PlacementServiceRegressionTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    [InlineData(0, 1.0)]
    [InlineData(-1, 1.0)]
    public void Dpi_factor_degrades_to_unscaled_instead_of_throwing(int dpi, double expected)
    {
        Assert.Equal(expected, MonitorPlacementService.ScaleFactorForDpi(dpi));
    }

    [Fact]
    public void Clamp_keeps_window_inside_remaining_work_area_after_unplug()
    {
        var monitors = new[]
        {
            new MonitorInfo("ONE", new PixelRect(0, 0, 1920, 1080), 96, true),
        };
        // Stale bounds from the unplugged second monitor.
        var stale = new PixelRect(2000, 100, 144, 144);

        var clamped = MonitorPlacementService.ClampToWorkArea(stale, monitors);

        Assert.True(clamped.IsValid);
        Assert.InRange(clamped.X, 0, 1920 - clamped.Width);
        Assert.InRange(clamped.Y, 0, 1080 - clamped.Height);
        Assert.Equal(144, clamped.Width);
        Assert.Equal(144, clamped.Height);
    }

    [Fact]
    public void Clamp_pins_overlarge_rect_to_area_origin()
    {
        var monitors = new[]
        {
            new MonitorInfo("ONE", new PixelRect(100, 100, 800, 600), 96, true),
        };

        var clamped = MonitorPlacementService.ClampToWorkArea(
            new PixelRect(0, 0, 2000, 2000),
            monitors);

        Assert.Equal(new PixelRect(100, 100, 800, 600), clamped);
    }

    [Fact]
    public void Clamp_throws_only_when_no_work_area_remains()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MonitorPlacementService.ClampToWorkArea(
                new PixelRect(0, 0, 100, 100),
                []));
    }

    [Fact]
    public void TryResolve_falls_back_to_clamp_instead_of_throwing()
    {
        var monitors = new[]
        {
            new MonitorInfo("ONE", new PixelRect(0, 0, 1920, 1080), 96, true),
        };
        var saved = new PetPlacement("MISSING-MONITOR", 0.8, 0.8, 1.0);

        // Unknown monitor name resolves to primary (true), but the out
        // contract must hold either way; with zero monitors it must clamp.
        var ok = MonitorPlacementService.TryResolve(
            saved,
            new PixelSize(192, 192),
            monitors,
            new PixelRect(2000, 100, 144, 144),
            out var resolution);

        Assert.True(ok);
        Assert.Equal("ONE", resolution.MonitorDeviceName);
        Assert.True(resolution.WindowBounds.IsValid);

        var stranded = MonitorPlacementService.TryResolve(
            saved,
            new PixelSize(192, 192),
            [],
            new PixelRect(2000, 100, 144, 144),
            out _);

        Assert.False(stranded);
    }

    [Fact]
    public void Logical_scale_is_preserved_across_resolve_and_capture()
    {
        var monitors = new[]
        {
            new MonitorInfo("ONE", new PixelRect(0, 0, 1920, 1080), 144, true),
        };
        var saved = new PetPlacement("ONE", 0.5, 0.5, 1.25);

        var resolved = MonitorPlacementService.Resolve(saved, new PixelSize(192, 192), monitors);
        var roundTripped = MonitorPlacementService.Capture(
            resolved.WindowBounds,
            resolved.Scale,
            new PixelSize(192, 192),
            monitors);

        // A DPI move must not reset the user's logical size: scale survives
        // the resolve/capture round trip used by the DPI-change path.
        Assert.Equal(1.25, roundTripped.Scale);
    }
}
