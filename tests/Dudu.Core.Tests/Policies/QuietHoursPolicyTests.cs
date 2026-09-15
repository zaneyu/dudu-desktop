using Dudu.Core.Models;
using Dudu.Core.Policies;
using Xunit;

namespace Dudu.Core.Tests.Policies;

public sealed class QuietHoursPolicyTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly QuietHours Quiet = new(
        Enabled: true,
        Start: new TimeOnly(22, 0),
        End: new TimeOnly(7, 0));

    [Theory]
    [InlineData("2026-09-11T23:00:00Z", true)]
    [InlineData("2026-09-12T06:59:00Z", true)]
    [InlineData("2026-09-12T07:00:00Z", false)]
    [InlineData("2026-09-12T12:00:00Z", false)]
    public void IsQuiet_handles_midnight_crossing(string utc, bool expected)
    {
        Assert.Equal(expected, QuietHoursPolicy.IsQuiet(DateTimeOffset.Parse(utc), Quiet, Utc));
    }

    [Fact]
    public void NextAllowedUtc_returns_the_next_end_boundary()
    {
        var result = QuietHoursPolicy.NextAllowedUtc(
            DateTimeOffset.Parse("2026-09-11T23:00:00Z"), Quiet, Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T07:00:00Z"), result);
    }

    [Theory]
    [InlineData("2026-09-11T00:00:00Z")]
    [InlineData("2026-09-11T12:00:00Z")]
    [InlineData("2026-09-11T23:59:00Z")]
    public void Zero_length_window_means_no_quiet_hours(string utc)
    {
        var zeroWindow = new QuietHours(Enabled: true, Start: new TimeOnly(22, 0), End: new TimeOnly(22, 0));

        Assert.False(QuietHoursPolicy.IsQuiet(DateTimeOffset.Parse(utc), zeroWindow, Utc));
    }

    [Fact]
    public void NextAllowedUtc_leaves_times_inside_a_zero_length_window_alone()
    {
        var zeroWindow = new QuietHours(Enabled: true, Start: new TimeOnly(22, 0), End: new TimeOnly(22, 0));
        var now = DateTimeOffset.Parse("2026-09-11T23:00:00Z");

        Assert.Equal(now, QuietHoursPolicy.NextAllowedUtc(now, zeroWindow, Utc));
    }
}
