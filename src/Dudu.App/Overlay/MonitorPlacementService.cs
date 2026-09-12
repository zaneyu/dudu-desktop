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
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2.0;

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
