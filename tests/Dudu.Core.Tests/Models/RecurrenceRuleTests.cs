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

    [Fact]
    public void SelectedWeekdays_with_null_Days_does_not_throw_and_is_treated_as_empty()
    {
        // Opus review regression: Days is declared non-nullable, but a `with`
        // expression (or a deserialized/corrupted row) can still hand back a
        // null set -- ReminderScheduler already guards against exactly this.
        // Equals/GetHashCode must not NullReferenceException in that case.
        var withNullDays = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday }, new TimeOnly(9, 0))
            with
        { Days = null! };
        var alsoNullDays = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Friday }, new TimeOnly(9, 0))
            with
        { Days = null! };
        var populatedDays = new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek> { DayOfWeek.Monday }, new TimeOnly(9, 0));

        Assert.Equal(withNullDays, alsoNullDays);
        Assert.Equal(withNullDays.GetHashCode(), alsoNullDays.GetHashCode());
        Assert.NotEqual(withNullDays, populatedDays);
        Assert.NotEqual(populatedDays, withNullDays);
        var hashCode = Record.Exception(() => withNullDays.GetHashCode());
        Assert.Null(hashCode);
    }
}
