namespace Dudu.Core.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
