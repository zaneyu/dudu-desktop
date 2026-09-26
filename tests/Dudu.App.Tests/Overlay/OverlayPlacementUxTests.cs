using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Overlay;

/// <summary>Regression coverage for where the pet ends up after a drag, a
/// DPI hop, or a taskbar move.</summary>
public sealed class OverlayPlacementUxTests
{
    private static readonly PixelSize Nominal = new(200, 200);

    // Primary 1920x1080 with a 48px bottom taskbar, and a 150% monitor to its right.
    private static readonly MonitorInfo Primary = new("PRIMARY", new PixelRect(0, 0, 1920, 1032), 96, IsPrimary: true);
    private static readonly MonitorInfo Right = new("RIGHT", new PixelRect(1920, 0, 2560, 1400), 144);

    [Fact]
    public void A_pet_dropped_half_off_the_left_edge_settles_fully_on_screen()
    {
        var settled = MonitorPlacementService.Settle(
            new PixelRect(-150, 300, 200, 200),
            1.0,
            Nominal,
            [Primary]);

        Assert.Equal("PRIMARY", settled.MonitorDeviceName);
        Assert.True(Primary.WorkArea.Contains(settled.WindowBounds));
        Assert.Equal(new PixelRect(0, 300, 200, 200), settled.WindowBounds);
    }

    [Fact]
    public void A_pet_dropped_over_the_taskbar_settles_above_it()
    {
        var settled = MonitorPlacementService.Settle(
            new PixelRect(800, 950, 200, 200),
            1.0,
            Nominal,
            [Primary, Right]);

        Assert.Equal(new PixelRect(800, 1032 - 200, 200, 200), settled.WindowBounds);
    }

    [Fact]
    public void A_pet_dropped_entirely_outside_every_monitor_comes_back_to_the_nearest_one()
    {
        var settled = MonitorPlacementService.Settle(
            new PixelRect(-5000, -5000, 200, 200),
            1.0,
            Nominal,
            [Primary, Right]);

        Assert.Equal("PRIMARY", settled.MonitorDeviceName);
        Assert.True(Primary.WorkArea.Contains(settled.WindowBounds));
    }

    [Fact]
    public void A_drop_already_inside_the_work_area_does_not_move()
    {
        var dropped = new PixelRect(2400, 500, 250, 250);

        var settled = MonitorPlacementService.Settle(dropped, 1.25, Nominal, [Primary, Right]);

        Assert.Equal("RIGHT", settled.MonitorDeviceName);
        Assert.Equal(dropped, settled.WindowBounds);
        Assert.Equal(1.25, settled.Scale);
    }

    [Fact]
    public void A_dpi_suggested_rect_keeps_the_logical_size_and_stays_centered()
    {
        // WM_DPICHANGED moving from 96 to 144 DPI suggests a 1.5x rectangle.
        // The pet keeps nominal x scale (what every later Resolve produces),
        // centered where Windows put it, so it no longer grows and then snaps
        // back on the next wheel resize or display change.
        var suggested = new PixelRect(2000, 400, 300, 300);

        var settled = MonitorPlacementService.Settle(suggested, 1.0, Nominal, [Primary, Right]);

        Assert.Equal(new PixelRect(2050, 450, 200, 200), settled.WindowBounds);
        var resolvedAgain = MonitorPlacementService.Resolve(
            MonitorPlacementService.ToPlacement(settled),
            Nominal,
            [Primary, Right]);
        Assert.Equal(settled.WindowBounds, resolvedAgain.WindowBounds);
    }

    [Fact]
    public void A_dpi_suggested_rect_hanging_off_the_new_monitor_is_pulled_back_in()
    {
        var suggested = new PixelRect(4300, 1300, 300, 300);

        var settled = MonitorPlacementService.Settle(suggested, 1.0, Nominal, [Primary, Right]);

        Assert.Equal("RIGHT", settled.MonitorDeviceName);
        Assert.True(Right.WorkArea.Contains(settled.WindowBounds));
    }

    [Theory]
    [InlineData(0x002F, true)]  // SPI_SETWORKAREA: taskbar moved/resized/auto-hide toggled
    [InlineData(0x0000, false)]
    [InlineData(0x0072, false)] // SPI_SETFONTSMOOTHINGTYPE and friends are unrelated
    public void Only_work_area_setting_changes_reflow_the_pet(int wParam, bool expected)
    {
        Assert.Equal(expected, OverlayWindowHost.IsWorkAreaChange(wParam));
    }

    [Fact]
    public void Drag_release_and_taskbar_changes_are_wired_into_the_window_procedure()
    {
        var source = ReadRepositoryFile("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");

        var up = Slice(source, "case WmLButtonUp:", "case WmLButtonDoubleClick:");
        Assert.True(
            up.IndexOf("SettleDraggedWindow();", StringComparison.Ordinal)
                < up.IndexOf("CommitPlacementIfDirty();", StringComparison.Ordinal),
            "A drop must settle into the work area before its placement is persisted.");
        Assert.Contains("SettleDraggedWindow();", Slice(source, "case WmCancelMode:", "case WmDestroy:"));
        Assert.Contains("case WmSettingChange when IsWorkAreaChange(", source);
        Assert.Contains("MonitorPlacementService.Settle(", Slice(source, "private void ApplyDpiSuggestedRect", "private void SettleDraggedWindow"));
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Expected '{start}'.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Expected '{end}' after '{start}'.");
        return source[from..to];
    }

    private static string ReadRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
