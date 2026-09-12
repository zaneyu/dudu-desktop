using Dudu.Core.Assets;
using Dudu.Core.Models;

namespace Dudu.App.Overlay;

public sealed record MonitorInfo(
    string DeviceName,
    PixelRect WorkArea,
    int Dpi,
    bool IsPrimary = false);

public readonly record struct PlacementResolution(
    string MonitorDeviceName,
    PixelRect WindowBounds,
    double Scale,
    double NormalizedX,
    double NormalizedY);

public static class MonitorPlacementService
{
    // PetPlacement.Scale is a user-facing logical size multiplier. Physical
    // monitor DPI is tracked for diagnostics and WM_DPICHANGED, but is not
    // multiplied into Resolve dimensions: the suggested RECT supplies the
    // physical resize and applying DPI here too would double-scale the pet.
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2.0;
    internal const uint HResultAccessDenied = 0x80070005;

    internal static int ResolveEffectiveDpi(
        uint hresult,
        uint dpiX,
        uint dpiY,
        Action<Exception>? report)
    {
        if ((hresult & 0x80000000) != 0)
        {
            if (hresult != HResultAccessDenied)
            {
                report?.Invoke(new InvalidOperationException(
                    $"GetDpiForMonitor failed with HRESULT 0x{hresult:X8}. Using 96 DPI."));
            }

            return 96;
        }

        if (dpiX == 0 || dpiY == 0 || dpiX > int.MaxValue || dpiY > int.MaxValue)
        {
            report?.Invoke(new InvalidOperationException(
                $"GetDpiForMonitor returned invalid DPI outputs ({dpiX}, {dpiY}). Using 96 DPI."));
            return 96;
        }

        return (int)dpiX;
    }

    public static double ClampScale(double scale)
    {
        if (double.IsNaN(scale))
        {
            return 1.0;
        }

        if (double.IsNegativeInfinity(scale))
        {
            return MinimumScale;
        }

        if (double.IsPositiveInfinity(scale))
        {
            return MaximumScale;
        }

        return Math.Clamp(scale, MinimumScale, MaximumScale);
    }

    public static PlacementResolution Resolve(
        PetPlacement saved,
        PixelSize nominalSize,
        IEnumerable<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(monitors);
        if (nominalSize.Width <= 0 || nominalSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize));
        }

        var usableMonitors = monitors
            .Where(monitor => monitor is not null && monitor.WorkArea.IsValid)
            .OrderBy(monitor => monitor.DeviceName, StringComparer.Ordinal)
            .ToArray();
        if (usableMonitors.Length == 0)
        {
            throw new InvalidOperationException("No monitor has a valid work area.");
        }

        var selected = usableMonitors.FirstOrDefault(monitor =>
            string.Equals(monitor.DeviceName, saved.MonitorDeviceName, StringComparison.Ordinal));
        if (selected is null)
        {
            selected = usableMonitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? usableMonitors[0];
        }

        var scale = ClampScale(saved.Scale);
        var width = ToDimension(nominalSize.Width, scale);
        var height = ToDimension(nominalSize.Height, scale);
        var normalizedX = ClampNormalized(saved.NormalizedX);
        var normalizedY = ClampNormalized(saved.NormalizedY);
        var workArea = selected.WorkArea;
        var x = Place(workArea.X, workArea.Width, width, normalizedX);
        var y = Place(workArea.Y, workArea.Height, height, normalizedY);

        return new PlacementResolution(
            selected.DeviceName,
            new PixelRect(x, y, width, height),
            scale,
            normalizedX,
            normalizedY);
    }

    public static PetPlacement ToPlacement(PlacementResolution resolution) =>
        new(
            resolution.MonitorDeviceName,
            resolution.NormalizedX,
            resolution.NormalizedY,
            resolution.Scale);

    public static PetPlacement Capture(
        PixelRect windowBounds,
        double scale,
        PixelSize nominalSize,
        IEnumerable<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (!windowBounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBounds));
        }

        if (nominalSize.Width <= 0 || nominalSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalSize));
        }

        var usable = monitors
            .Where(monitor => monitor is not null && monitor.WorkArea.IsValid)
            .OrderBy(monitor => monitor.DeviceName, StringComparer.Ordinal)
            .ToArray();
        if (usable.Length == 0)
        {
            throw new InvalidOperationException("No monitor has a valid work area.");
        }

        var centerX = (long)windowBounds.X + windowBounds.Width / 2L;
        var centerY = (long)windowBounds.Y + windowBounds.Height / 2L;
        var selected = usable.FirstOrDefault(monitor =>
            centerX >= monitor.WorkArea.X
            && centerX < monitor.WorkArea.Right
            && centerY >= monitor.WorkArea.Y
            && centerY < monitor.WorkArea.Bottom)
            ?? usable
                .OrderBy(monitor => DistanceSquared(monitor.WorkArea, centerX, centerY))
                .ThenBy(monitor => monitor.DeviceName, StringComparer.Ordinal)
                .First();

        var availableWidth = (long)selected.WorkArea.Width - windowBounds.Width;
        var availableHeight = (long)selected.WorkArea.Height - windowBounds.Height;
        var normalizedX = availableWidth <= 0
            ? 0.5
            : ClampNormalized((windowBounds.X - (double)selected.WorkArea.X) / availableWidth);
        var normalizedY = availableHeight <= 0
            ? 0.5
            : ClampNormalized((windowBounds.Y - (double)selected.WorkArea.Y) / availableHeight);

        return new PetPlacement(
            selected.DeviceName,
            normalizedX,
            normalizedY,
            ClampScale(scale));
    }

    private static double DistanceSquared(PixelRect area, long x, long y)
    {
        var dx = x < area.X ? area.X - x : x >= area.Right ? x - area.Right + 1 : 0;
        var dy = y < area.Y ? area.Y - y : y >= area.Bottom ? y - area.Bottom + 1 : 0;
        return (double)dx * dx + (double)dy * dy;
    }

    private static int ToDimension(int value, double scale) =>
        Math.Max(1, checked((int)Math.Round(value * scale, MidpointRounding.AwayFromZero)));

    private static int Place(int origin, int extent, int size, double normalized)
    {
        var available = (long)extent - size;
        if (available <= 0)
        {
            return origin;
        }

        var offset = (long)Math.Round(available * normalized, MidpointRounding.AwayFromZero);
        return checked((int)Math.Clamp((long)origin + offset, origin, (long)origin + available));
    }

    private static double ClampNormalized(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
}
