using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class MonitorPlacementServiceTests
{
    [Fact]
    public void Production_nominal_size_is_reduced_without_changing_user_scale()
    {
        Assert.Equal(
            new PixelSize(384, 384),
            MonitorPlacementService.ScaleNominalSize(new PixelSize(512, 512)));
    }

    [Fact]
    public void Missing_saved_monitor_moves_pet_into_primary_work_area()
    {
        var monitors = new[]
        {
            new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 1920, 1040), 144, IsPrimary: true),
        };
        var saved = new PetPlacement("REMOVED", 0.95, 0.95, 1.0);

        var result = MonitorPlacementService.Resolve(saved, new PixelSize(512, 512), monitors);

        Assert.Equal("DISPLAY1", result.MonitorDeviceName);
        Assert.True(monitors[0].WorkArea.Contains(result.WindowBounds));
    }

    [Fact]
    public void Scale_is_clamped_between_half_and_double_size()
    {
        Assert.Equal(0.5, MonitorPlacementService.ClampScale(0.1));
        Assert.Equal(2.0, MonitorPlacementService.ClampScale(4.0));
    }

    [Fact]
    public void Invalid_geometry_is_ignored_and_primary_selection_is_deterministic()
    {
        var monitors = new[]
        {
            new MonitorInfo("Z", new PixelRect(1000, 0, 0, 1000), 96),
            new MonitorInfo("B", new PixelRect(1920, 0, 1920, 1040), 144, IsPrimary: true),
            new MonitorInfo("A", new PixelRect(0, 0, 1920, 1040), 96),
        };

        var result = MonitorPlacementService.Resolve(
            new PetPlacement("MISSING", double.NaN, double.PositiveInfinity, double.NaN),
            new PixelSize(512, 512),
            monitors);

        Assert.Equal("B", result.MonitorDeviceName);
        Assert.Equal(1.0, result.Scale);
        Assert.True(monitors[1].WorkArea.Contains(result.WindowBounds));
    }

    [Fact]
    public void Placement_clamps_a_large_pet_to_the_work_area_origin()
    {
        var monitor = new MonitorInfo("DISPLAY1", new PixelRect(-100, 20, 400, 300), 96, true);
        var result = MonitorPlacementService.Resolve(
            new PetPlacement("DISPLAY1", 1, 1, 2),
            new PixelSize(512, 512),
            [monitor]);

        Assert.Equal(new PixelRect(-100, 20, 1024, 1024), result.WindowBounds);
    }

    [Fact]
    public void Normalized_coordinates_are_preserved_after_resolution()
    {
        var result = MonitorPlacementService.Resolve(
            new PetPlacement("DISPLAY1", 0.25, 0.75, 1),
            new PixelSize(100, 80),
            [new MonitorInfo("DISPLAY1", new PixelRect(10, 20, 1000, 800), 96, true)]);

        Assert.Equal(0.25, result.NormalizedX);
        Assert.Equal(0.75, result.NormalizedY);
        Assert.Equal(new PixelRect(235, 560, 100, 80), result.WindowBounds);
    }

    [Fact]
    public void No_valid_monitor_is_a_clear_error()
    {
        Assert.Throws<InvalidOperationException>(() => MonitorPlacementService.Resolve(
            new PetPlacement("DISPLAY1", 0, 0, 1),
            new PixelSize(1, 1),
            [new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 0, 0), 96)]));
    }

    [Fact]
    public void Capture_persists_negative_secondary_monitor_coordinates_after_drag()
    {
        var monitors = new[]
        {
            new MonitorInfo("PRIMARY", new PixelRect(0, 0, 1920, 1040), 96, true),
            new MonitorInfo("LEFT", new PixelRect(-1280, 0, 1280, 1024), 144),
        };

        var placement = MonitorPlacementService.Capture(
            new PixelRect(-1000, 120, 240, 200),
            1.25,
            new PixelSize(240, 200),
            monitors);

        Assert.Equal("LEFT", placement.MonitorDeviceName);
        Assert.Equal(1.25, placement.Scale);
        Assert.InRange(placement.NormalizedX, 0, 1);
        Assert.InRange(placement.NormalizedY, 0, 1);

        var restored = MonitorPlacementService.Resolve(placement, new PixelSize(240, 200), monitors);
        Assert.Equal("LEFT", restored.MonitorDeviceName);
        Assert.True(monitors[1].WorkArea.Contains(restored.WindowBounds));
    }

    [Fact]
    public void Capture_uses_deterministic_device_name_order_for_equal_monitor_candidates()
    {
        var monitors = new[]
        {
            new MonitorInfo("Z", new PixelRect(0, 0, 800, 800), 96),
            new MonitorInfo("A", new PixelRect(0, 0, 800, 800), 96),
        };

        var placement = MonitorPlacementService.Capture(
            new PixelRect(100, 100, 100, 100),
            1,
            new PixelSize(100, 100),
            monitors);

        Assert.Equal("A", placement.MonitorDeviceName);
    }

    [Fact]
    public void Capture_snapshot_monitor_matches_the_monitor_selected_from_actual_bounds()
    {
        var monitors = new[]
        {
            new MonitorInfo("PRIMARY", new PixelRect(0, 0, 1920, 1040), 96, true),
            new MonitorInfo("RIGHT", new PixelRect(1920, 0, 1920, 1040), 144),
        };

        var snapshot = MonitorPlacementService.CaptureSnapshot(
            new PixelRect(2200, 120, 240, 200),
            1.25,
            new PixelSize(240, 200),
            monitors);

        Assert.Equal("RIGHT", snapshot.Placement.MonitorDeviceName);
        Assert.Equal("RIGHT", snapshot.Monitor.DeviceName);
        Assert.Equal(new PixelRect(2200, 120, 240, 200), snapshot.WindowBounds);
    }

    [Theory]
    [InlineData(0x80070005u, uint.MaxValue, 0u, false)]
    [InlineData(0x80004005u, 0u, uint.MaxValue, true)]
    public void Failed_dpi_query_discards_out_values_and_uses_96_dpi(
        uint hresult,
        uint dpiX,
        uint dpiY,
        bool reportsUnexpectedFailure)
    {
        var reports = 0;

        var effectiveDpi = MonitorPlacementService.ResolveEffectiveDpi(
            hresult,
            dpiX,
            dpiY,
            _ => reports++);

        Assert.Equal(96, effectiveDpi);
        Assert.Equal(reportsUnexpectedFailure ? 1 : 0, reports);
    }
}
