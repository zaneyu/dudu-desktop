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
    [InlineData(96, 384)]
    [InlineData(120, 480)]
    [InlineData(144, 576)]
    [InlineData(192, 768)]
    public void Resolve_sizes_the_pet_for_the_monitor_dpi(int dpi, int expectedSide)
    {
        // Finding: Resolve used to size nominal*scale physical pixels on every
        // monitor, so on 150-200% displays Dudu rendered at 1/1.5-1/2 of her
        // intended logical size.
        var result = MonitorPlacementService.Resolve(
            new PetPlacement("DISPLAY1", 0.5, 0.5, 1.0),
            new PixelSize(384, 384),
            [new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 3840, 2100), dpi, true)]);

        Assert.Equal(expectedSide, result.WindowBounds.Width);
        Assert.Equal(expectedSide, result.WindowBounds.Height);
        Assert.Equal(1.0, result.Scale);
    }

    [Fact]
    public void Dpi_sizing_multiplies_with_the_user_scale_and_ignores_invalid_dpi()
    {
        Assert.Equal(
            new PixelSize(576, 432),
            MonitorPlacementService.ScaleWindowSize(new PixelSize(256, 192), 1.5, 144));
        Assert.Equal(
            new PixelSize(256, 192),
            MonitorPlacementService.ScaleWindowSize(new PixelSize(256, 192), 1.0, 0));
        Assert.Equal(
            new PixelSize(512, 384),
            MonitorPlacementService.ScaleWindowSize(new PixelSize(256, 192), 99, 96));
    }

    [Fact]
    public void Capture_then_resolve_round_trips_on_a_high_dpi_monitor()
    {
        var monitors = new[]
        {
            new MonitorInfo("PRIMARY", new PixelRect(0, 0, 1920, 1040), 96, true),
            new MonitorInfo("HIDPI", new PixelRect(1920, 0, 3840, 2100), 192),
        };
        var nominal = new PixelSize(384, 384);
        var size = MonitorPlacementService.ScaleWindowSize(nominal, 1.25, 192);
        var bounds = new PixelRect(2500, 700, size.Width, size.Height);

        var placement = MonitorPlacementService.Capture(bounds, 1.25, nominal, monitors);
        var restored = MonitorPlacementService.Resolve(placement, nominal, monitors);

        Assert.Equal("HIDPI", placement.MonitorDeviceName);
        Assert.Equal(bounds, restored.WindowBounds);
    }

    [Fact]
    public void Wheel_delta_scales_proportionally_so_touchpads_do_not_snap_to_the_limits()
    {
        // A full 120-unit notch keeps the historic x1.1 step.
        Assert.Equal(1.1, MonitorPlacementService.ApplyWheelDelta(1.0, 120), 10);
        Assert.Equal(1 / 1.1, MonitorPlacementService.ApplyWheelDelta(1.0, -120), 10);

        // Ten 12-unit touchpad deltas add up to exactly one notch instead of
        // ten notches (1.1^10 = 2.59 -> clamped straight to the maximum).
        var scale = 1.0;
        for (var index = 0; index < 10; index++)
        {
            scale = MonitorPlacementService.ApplyWheelDelta(scale, 12);
        }

        Assert.Equal(1.1, scale, 10);
        Assert.Equal(1.0, MonitorPlacementService.ApplyWheelDelta(1.0, 0));
        Assert.Equal(MonitorPlacementService.MaximumScale, MonitorPlacementService.ApplyWheelDelta(1.9, 1200));
        Assert.Equal(MonitorPlacementService.MinimumScale, MonitorPlacementService.ApplyWheelDelta(0.6, -1200));
    }

    [Fact]
    public void Dragged_bounds_are_clamped_inside_the_work_area()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        Assert.Equal(
            new PixelRect(0, 0, 384, 384),
            MonitorPlacementService.ClampToWorkArea(new PixelRect(-200, -50, 384, 384), workArea));
        Assert.Equal(
            new PixelRect(1536, 656, 384, 384),
            MonitorPlacementService.ClampToWorkArea(new PixelRect(1800, 1000, 384, 384), workArea));
        Assert.Equal(
            new PixelRect(100, 200, 384, 384),
            MonitorPlacementService.ClampToWorkArea(new PixelRect(100, 200, 384, 384), workArea));
        // Larger than the work area: pinned to its origin like Resolve.
        Assert.Equal(
            new PixelRect(-1280, 0, 2000, 2000),
            MonitorPlacementService.ClampToWorkArea(
                new PixelRect(-900, 40, 2000, 2000),
                new PixelRect(-1280, 0, 1280, 1024)));
    }

    [Fact]
    public void Clamped_drag_keeps_the_saved_placement_equal_to_the_window()
    {
        // Capture clamps normalized coordinates; an unclamped off-screen
        // window used to persist a placement that restored somewhere else.
        var monitors = new[] { new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 1920, 1040), 96, true) };
        var clamped = MonitorPlacementService.ClampToWorkArea(
            new PixelRect(1850, -120, 384, 384),
            monitors[0].WorkArea);

        var placement = MonitorPlacementService.Capture(clamped, 1, new PixelSize(384, 384), monitors);
        var restored = MonitorPlacementService.Resolve(placement, new PixelSize(384, 384), monitors);

        Assert.Equal(clamped, restored.WindowBounds);
    }

    [Fact]
    public void Monitor_under_the_cursor_is_selected_else_the_nearest()
    {
        var monitors = new[]
        {
            new MonitorInfo("PRIMARY", new PixelRect(0, 0, 1920, 1040), 96, true),
            new MonitorInfo("RIGHT", new PixelRect(1920, 0, 1920, 1040), 144),
        };

        Assert.Equal("RIGHT", MonitorPlacementService.SelectMonitorAt(monitors, new PixelPoint(2000, 10)).DeviceName);
        Assert.Equal("PRIMARY", MonitorPlacementService.SelectMonitorAt(monitors, new PixelPoint(10, 10)).DeviceName);
        // Over the primary taskbar (outside every work area).
        Assert.Equal("PRIMARY", MonitorPlacementService.SelectMonitorAt(monitors, new PixelPoint(300, 1070)).DeviceName);
    }

    [Fact]
    public void Resize_around_point_keeps_the_grab_point_under_the_cursor()
    {
        var resized = MonitorPlacementService.ResizeAroundPoint(
            new PixelRect(100, 100, 400, 400),
            new PixelPoint(200, 300),
            new PixelSize(600, 600));

        // The cursor was 25% across and 50% down; it still is.
        Assert.Equal(new PixelRect(50, 0, 600, 600), resized);
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
