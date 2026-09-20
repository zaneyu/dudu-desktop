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
    public void Interval_rule_formats_whole_days_as_day_week_or_n_days()
    {
        // Opus review follow-up: whole-day intervals read as "every 1440
        // minutes" instead of a sensible day/week phrasing.
        Assert.Equal(
            "every day",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromDays(1))));
        Assert.Equal(
            "every week",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromDays(7))));
        Assert.Equal(
            "every 3 days",
            ReminderScheduleSummary.Describe(new RecurrenceRule.Interval(TimeSpan.FromDays(3))));
    }

    [Fact]
    public void Once_rule_formats_as_once()
    {
        Assert.Equal("once", ReminderScheduleSummary.Describe(new RecurrenceRule.Once()));
    }

    [Fact]
    public void Selected_weekdays_rule_with_an_empty_day_set_falls_back_to_time_only_copy()
    {
        // Audit regression: an empty weekday set rendered "on  at 9:00 am"
        // (a blank day list) instead of copy consistent with the rest of
        // this summary.
        Assert.Equal(
            "every week at 9:00 am",
            ReminderScheduleSummary.Describe(new RecurrenceRule.SelectedWeekdays(
                new HashSet<DayOfWeek>(),
                new TimeOnly(9, 0))));
    }

    [Fact]
    public void Selected_weekdays_rule_with_a_null_day_set_does_not_throw()
    {
        // Audit regression: this runs inside an x:Bind function binding
        // during ListView item realization, where a throw crashes the page
        // -- corrupt/old data with a null weekday set must not crash it.
        Assert.Equal(
            "every week at 9:00 am",
            ReminderScheduleSummary.Describe(new RecurrenceRule.SelectedWeekdays(
                (IReadOnlySet<DayOfWeek>)null!,
                new TimeOnly(9, 0))));
    }
}
