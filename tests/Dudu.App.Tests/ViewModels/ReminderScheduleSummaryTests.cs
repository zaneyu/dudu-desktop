using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class ReminderScheduleSummaryTests
{
    // Audit regression: the reminders list showed the literal text "reminder
    // schedule" for every row instead of a real, human, local-time summary.

    [Fact]
    public void Daily_rule_formats_as_lowercase_local_time()
    {
        Assert.Equal(
            "every day at 9:30 pm",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Daily(new TimeOnly(21, 30))));
    }

    [Fact]
    public void Selected_weekdays_rule_orders_days_monday_first()
    {
        Assert.Equal(
            "on mon, wed, fri at 9:00 am",
            ReminderScheduleSummary.Describe(new RecurrenceRule.SelectedWeekdays(
                new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday },
                new TimeOnly(9, 0))));
    }

    [Fact]
    public void Interval_rule_formats_whole_hours_and_leftover_minutes_differently()
    {
        Assert.Equal(
            "every 2 hours",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromHours(2))));
        Assert.Equal(
            "every 1 hour",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromHours(1))));
        Assert.Equal(
            "every 90 minutes",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromMinutes(90))));
    }

    [Fact]
    public void Once_rule_formats_as_once()
    {
        Assert.Equal("once", ReminderScheduleSummary.Describe(new RecurrenceRule.Once()));
    }
}
