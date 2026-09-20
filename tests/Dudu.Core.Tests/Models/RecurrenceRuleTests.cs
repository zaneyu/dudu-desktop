using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests.Models;

public sealed class RecurrenceRuleTests
{
    [Fact]
    public void SelectedWeekdays_with_the_same_days_in_different_set_instances_are_equal()
    {
        // Opus review regression: the record-synthesized equality compared Days
        // by reference (HashSet<T> does not override Equals/GetHashCode), so an
        // "unchanged rule" check comparing two freshly-built SelectedWeekdays
        // rules with identical days would silently never match.
        var a = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            new TimeOnly(9, 0));
        var b = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday },
            new TimeOnly(9, 0));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True((RecurrenceRule)a == (RecurrenceRule)b);
    }

    [Fact]
    public void SelectedWeekdays_with_different_days_or_times_are_not_equal()
    {
        var baseline = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday },
            new TimeOnly(9, 0));
        var differentDays = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday },
            new TimeOnly(9, 0));
        var differentTime = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday },
            new TimeOnly(10, 0));

        Assert.NotEqual(baseline, differentDays);
        Assert.NotEqual(baseline, differentTime);
    }
}
