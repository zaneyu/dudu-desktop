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

    [Fact]
    public void Evening_and_bedtime_routines_are_opt_in_and_keep_their_evening_due()
    {
        var preferences = Preferences.Default with
        {
            QuietHours = new QuietHours(true, new TimeOnly(21, 0), new TimeOnly(7, 0)),
            EveningCheckInEnabled = true,
            BedtimeRitualEnabled = true,
        };

        var reminders = LocalReminderDefaults.Create(
            preferences,
            DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
            TimeZoneInfo.Utc);

        var evening = reminders.Single(reminder => reminder.Id == LocalReminderDefaults.EveningCheckInId);
        var bedtime = reminders.Single(reminder => reminder.Id == LocalReminderDefaults.BedtimeId);
        Assert.Equal("how was your day, ada?", evening.Title);
        Assert.Equal("shuijiaojiao, ada", bedtime.Title);
        Assert.Equal(new RecurrenceRule.Daily(new TimeOnly(20, 0)), evening.Rule);
        Assert.Equal(new RecurrenceRule.Daily(new TimeOnly(22, 0)), bedtime.Rule);

        // Both routines are scheduled inside the quiet window and must keep the
        // raw 20:00/22:00 due: the presentation gateway suppresses delivery while
        // quiet hours are active, and shifting here would land them the morning after.
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T20:00:00Z"), evening.NextDueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T22:00:00Z"), bedtime.NextDueUtc);
        Assert.All(new[] { evening, bedtime }, reminder =>
        {
            Assert.Null(reminder.QuietHours);
            Assert.Equal(QuietHoursBehavior.WaitUntilQuietHoursEnd, reminder.QuietHoursBehavior);
            Assert.Equal(MissedOccurrencePolicy.Skip, reminder.MissedPolicy);
        });

        var optedOut = LocalReminderDefaults.Create(
            Preferences.Default with { QuietHours = preferences.QuietHours },
            DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
            TimeZoneInfo.Utc);
        Assert.All(optedOut.Where(reminder => reminder.Id is LocalReminderDefaults.EveningCheckInId
            or LocalReminderDefaults.BedtimeId), reminder => Assert.False(reminder.Enabled));
    }

    [Fact]
    public void Routine_dues_resolve_across_the_spring_forward_transition()
    {
        var zone = FindPacificTimeZone();

        var reminders = LocalReminderDefaults.Create(
            Preferences.Default with
            {
                EveningCheckInEnabled = true,
                BedtimeRitualEnabled = true,
            },
            // 2026-03-07 10:00 PST; the next 20:00/22:00 local are 2026-03-08 PDT.
            DateTimeOffset.Parse("2026-03-07T18:00:00Z"),
            zone);

        var evening = reminders.Single(reminder => reminder.Id == LocalReminderDefaults.EveningCheckInId);
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T04:00:00Z"), evening.NextDueUtc);
        var bedtime = reminders.Single(reminder => reminder.Id == LocalReminderDefaults.BedtimeId);
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T06:00:00Z"), bedtime.NextDueUtc);
    }

    [Fact]
    public void Routine_dues_take_the_earlier_instant_of_the_fall_back_hour()
    {
        var zone = FindPacificTimeZone();

        var reminders = LocalReminderDefaults.Create(
            Preferences.Default with { EveningCheckInEnabled = true },
            // 2026-11-01 08:00 PDT; the next 20:00 local is 2026-11-01 20:00 PST.
            DateTimeOffset.Parse("2026-11-01T15:00:00Z"),
            zone);

        var evening = reminders.Single(reminder => reminder.Id == LocalReminderDefaults.EveningCheckInId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T04:00:00Z"), evening.NextDueUtc);
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
