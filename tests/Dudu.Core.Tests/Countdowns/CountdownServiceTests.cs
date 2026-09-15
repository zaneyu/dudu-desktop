using Dudu.Core.Countdowns;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests.Countdowns;

public sealed class CountdownServiceTests
{
    [Fact]
    public void Missing_targets_return_a_zero_display()
    {
        var countdown = new Countdown("empty", "Empty");

        var display = CountdownService.GetDisplay(
            countdown,
            DateTimeOffset.Parse("2026-09-11T10:00:00Z"));

        Assert.Equal(TimeSpan.Zero, display.Remaining);
        Assert.Equal(0, display.CalendarDays);
    }

    [Fact]
    public void All_day_without_either_target_returns_a_zero_display()
    {
        var countdown = new Countdown("empty-all-day", "Empty", (DateTimeOffset?)null, isAllDay: true);

        var display = CountdownService.GetDisplay(
            countdown,
            DateTimeOffset.Parse("2026-09-11T10:00:00Z"));

        Assert.Equal(TimeSpan.Zero, display.Remaining);
        Assert.Equal(0, display.CalendarDays);
    }
}
