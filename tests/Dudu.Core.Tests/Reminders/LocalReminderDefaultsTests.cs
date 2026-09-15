using Dudu.Core.Models;
using Dudu.Core.Reminders;
using Xunit;

namespace Dudu.Core.Tests.Reminders;

public sealed class LocalReminderDefaultsTests
{
    [Fact]
    public void Initial_due_defers_through_quiet_hours()
    {
        var preferences = Preferences.Default with
        {
            QuietHours = new QuietHours(true, new TimeOnly(9, 0), new TimeOnly(11, 0)),
        };

        var reminders = LocalReminderDefaults.Create(
            preferences,
            DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
            TimeZoneInfo.Utc);

        var hydration = reminders.Single(reminder => reminder.Id == "default-hydration");
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T11:00:00Z"), hydration.NextDueUtc);

        var pause = reminders.Single(reminder => reminder.Id == "default-break");
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T14:00:00Z"), pause.NextDueUtc);
    }

    [Fact]
    public void Defaults_resolve_across_the_spring_forward_transition()
    {
        var zone = FindPacificTimeZone();

        var reminders = LocalReminderDefaults.Create(
            Preferences.Default,
            // 2026-03-07 10:00 PST; the next 10:00 local is 2026-03-08 10:00 PDT.
            DateTimeOffset.Parse("2026-03-07T18:00:00Z"),
            zone);

        var hydration = reminders.Single(reminder => reminder.Id == "default-hydration");
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T17:00:00Z"), hydration.NextDueUtc);
    }

    [Fact]
    public void Defaults_take_the_earlier_instant_of_the_fall_back_hour()
    {
        var zone = FindPacificTimeZone();

        var reminders = LocalReminderDefaults.Create(
            Preferences.Default,
            // 2026-11-01 08:00 PST; the next 10:00 local is 2026-11-01 10:00 PST.
            DateTimeOffset.Parse("2026-11-01T16:00:00Z"),
            zone);

        var hydration = reminders.Single(reminder => reminder.Id == "default-hydration");
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T18:00:00Z"), hydration.NextDueUtc);
    }

    private static TimeZoneInfo FindPacificTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
    }
}
