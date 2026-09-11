using Dudu.Core.Models;
using Dudu.Core.Policies;
using Dudu.Core.Reminders;
using Xunit;

namespace Dudu.Core.Tests.Reminders;

public sealed class ReminderSchedulerTests
{
    [Fact]
    public void Selected_weekdays_skips_unselected_days()
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0)
            .On(DayOfWeek.Monday, DayOfWeek.Wednesday)
            .Build();

        var next = ReminderScheduler.NextOccurrence(
            reminder,
            DateTimeOffset.Parse("2026-09-14T10:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-16T09:00:00Z"), next);
    }

    [Fact]
    public void Daily_recurrence_advances_after_a_due_occurrence()
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build();

        var next = ReminderScheduler.NextOccurrence(
            reminder,
            DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), next);
    }

    [Fact]
    public void Interval_recurrence_advances_without_emitting_a_burst()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build();

        var next = ReminderScheduler.NextOccurrence(
            reminder,
            DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T20:00:00Z"), next);
    }

    [Fact]
    public void Hydration_reconciliation_emits_only_one_occurrence_after_sleep()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2))
            .WithMissedPolicy(MissedOccurrencePolicy.LatestOnly)
            .Build();

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Single(result.DueNow);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T20:00:00Z"), result.NextUtc);
    }

    [Theory]
    [InlineData("daily_after_due", "2026-09-12T09:00:00Z")]
    [InlineData("quiet_at_2300", "2026-09-12T07:00:00Z")]
    [InlineData("spring_forward_0230", "2026-03-08T10:00:00Z")]
    [InlineData("snoozed_until_1015", "2026-09-11T10:15:00Z")]
    public void Next_occurrence_matches_policy_fixture(string fixtureName, string expectedUtc)
    {
        var fixture = ReminderPolicyFixture.Load(fixtureName);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc), fixture.NextOccurrence());
    }

    [Fact]
    public void Expired_one_time_reminder_has_no_next_occurrence()
    {
        var fixture = ReminderPolicyFixture.Load("expired_once");
        Assert.Null(fixture.NextOccurrence());
    }

    private sealed class ReminderBuilder
    {
        private RecurrenceRule _rule;
        private MissedOccurrencePolicy _missedPolicy = MissedOccurrencePolicy.LatestOnly;
        private QuietHoursBehavior _quietHoursBehavior = QuietHoursBehavior.DeliverImmediately;
        private QuietHours? _quietHours;
        private DateTimeOffset _nextDueUtc = DateTimeOffset.Parse("2026-09-11T09:00:00Z");
        private DateTimeOffset? _snoozedUntilUtc;

        private ReminderBuilder(RecurrenceRule rule)
        {
            _rule = rule;
        }

        public static ReminderBuilder AtLocalTime(int hour, int minute)
        {
            var builder = new ReminderBuilder(new RecurrenceRule.Daily(new TimeOnly(hour, minute)));
            builder._nextDueUtc = new DateTimeOffset(
                new DateTime(2026, 9, 11, hour, minute, 0, DateTimeKind.Unspecified),
                TimeSpan.Zero);
            return builder;
        }

        public static ReminderBuilder Every(TimeSpan interval)
        {
            var builder = new ReminderBuilder(new RecurrenceRule.Interval(
                interval,
                DateTimeOffset.Parse("2026-09-11T08:00:00Z")));
            builder._nextDueUtc = DateTimeOffset.Parse("2026-09-11T08:00:00Z");
            return builder;
        }

        public ReminderBuilder On(params DayOfWeek[] days)
        {
            _rule = new RecurrenceRule.SelectedWeekdays(
                new HashSet<DayOfWeek>(days),
                new TimeOnly(9, 0));
            return this;
        }

        public ReminderBuilder WithMissedPolicy(MissedOccurrencePolicy missedPolicy)
        {
            _missedPolicy = missedPolicy;
            return this;
        }

        public ReminderBuilder WithQuietHours(QuietHours quietHours)
        {
            _quietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd;
            _quietHours = quietHours;
            return this;
        }

        public ReminderBuilder SnoozedUntil(DateTimeOffset snoozedUntilUtc)
        {
            _snoozedUntilUtc = snoozedUntilUtc;
            return this;
        }

        public Reminder Build()
        {
            return new Reminder(
                "reminder-1",
                "Reminder",
                null,
                true,
                _rule,
                "UTC",
                _quietHoursBehavior,
                _missedPolicy,
                _nextDueUtc,
                _snoozedUntilUtc,
                _quietHours);
        }
    }

    private sealed class ReminderPolicyFixture
    {
        private readonly Reminder _reminder;
        private readonly DateTimeOffset _nowUtc;
        private readonly TimeZoneInfo _timeZone;

        private ReminderPolicyFixture(
            Reminder reminder,
            DateTimeOffset nowUtc,
            TimeZoneInfo timeZone)
        {
            _reminder = reminder;
            _nowUtc = nowUtc;
            _timeZone = timeZone;
        }

        public static ReminderPolicyFixture Load(string name)
        {
            return name switch
            {
                "daily_after_due" => new(
                    ReminderBuilder.AtLocalTime(9, 0).Build(),
                    DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
                    TimeZoneInfo.Utc),
                "quiet_at_2300" => new(
                    ReminderBuilder.AtLocalTime(23, 0)
                        .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
                        .Build(),
                    DateTimeOffset.Parse("2026-09-11T23:00:00Z"),
                    TimeZoneInfo.Utc),
                "spring_forward_0230" => new(
                    new Reminder(
                        "spring",
                        "Spring forward",
                        null,
                        true,
                        new RecurrenceRule.Daily(new TimeOnly(2, 30)),
                        "Pacific Standard Time",
                        QuietHoursBehavior.DeliverImmediately,
                        MissedOccurrencePolicy.LatestOnly,
                        DateTimeOffset.Parse("2026-03-07T10:30:00Z")),
                    DateTimeOffset.Parse("2026-03-07T12:00:00Z"),
                    FindPacificTimeZone()),
                "snoozed_until_1015" => new(
                    ReminderBuilder.AtLocalTime(9, 0)
                        .SnoozedUntil(DateTimeOffset.Parse("2026-09-11T10:15:00Z"))
                        .Build(),
                    DateTimeOffset.Parse("2026-09-11T09:30:00Z"),
                    TimeZoneInfo.Utc),
                "expired_once" => new(
                    new Reminder(
                        "once",
                        "Expired",
                        null,
                        true,
                        new RecurrenceRule.Once(),
                        "UTC",
                        QuietHoursBehavior.DeliverImmediately,
                        MissedOccurrencePolicy.LatestOnly,
                        DateTimeOffset.Parse("2026-09-10T09:00:00Z")),
                    DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
                    TimeZoneInfo.Utc),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown reminder policy fixture."),
            };
        }

        public DateTimeOffset? NextOccurrence()
        {
            return ReminderScheduler.NextOccurrence(_reminder, _nowUtc, _timeZone);
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
}
