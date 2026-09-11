namespace Dudu.Core.Models;

public sealed record PetPlacement(
    string MonitorDeviceName,
    double NormalizedX,
    double NormalizedY,
    double Scale);
