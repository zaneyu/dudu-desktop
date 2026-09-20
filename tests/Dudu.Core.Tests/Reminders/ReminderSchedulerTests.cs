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
    public void Zero_length_quiet_window_does_not_defer_a_due_reminder()
    {
        var reminder = ReminderBuilder.AtLocalTime(22, 30)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(22, 0)))
            .Build();
        var nowUtc = DateTimeOffset.Parse("2026-09-11T22:30:00Z");

        var next = ReminderScheduler.NextOccurrence(
            reminder,
            DateTimeOffset.Parse("2026-09-11T20:00:00Z"),
            TimeZoneInfo.Utc);
        var result = ReminderScheduler.Reconcile(
            reminder,
            nowUtc,
            nowUtc,
            TimeZoneInfo.Utc);

        Assert.Equal(nowUtc, next);
        Assert.Equal(nowUtc, Assert.Single(result.DueNow).DueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T22:30:00Z"), result.NextUtc);
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

    [Fact]
    public void Completed_once_reminder_with_no_due_time_stays_completed()
    {
        var reminder = new Reminder(
            "completed", "Completed", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly, null);

        Assert.Null(ReminderScheduler.NextOccurrence(
            reminder, DateTimeOffset.Parse("2026-09-11T10:00:00Z"), TimeZoneInfo.Utc));
        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            TimeZoneInfo.Utc);
        Assert.Empty(result.DueNow);
        Assert.Null(result.NextUtc);
    }

    [Fact]
    public void Daily_occurrence_at_exact_now_advances_to_the_next_day()
    {
        var now = DateTimeOffset.Parse("2026-09-11T09:00:00Z");
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build();

        var next = ReminderScheduler.NextOccurrence(reminder, now, TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), next);
    }

    [Fact]
    public void Interval_occurrence_at_exact_now_advances_by_one_period()
    {
        var now = DateTimeOffset.Parse("2026-09-11T08:00:00Z");
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build();

        var next = ReminderScheduler.NextOccurrence(reminder, now, TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T10:00:00Z"), next);
    }

    [Fact]
    public void Deferred_occurrence_is_delivered_at_quiet_hours_end_and_then_recurrence_advances()
    {
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build();
        var quietTime = DateTimeOffset.Parse("2026-09-11T23:00:00Z");
        var quietResult = ReminderScheduler.Reconcile(
            reminder,
            quietTime,
            quietTime,
            TimeZoneInfo.Utc);

        Assert.Empty(quietResult.DueNow);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T07:00:00Z"), quietResult.NextUtc);

        var deferredReminder = reminder with { NextDueUtc = quietResult.NextUtc!.Value };
        var allowedTime = quietResult.NextUtc.Value;
        var allowedResult = ReminderScheduler.Reconcile(
            deferredReminder,
            allowedTime,
            allowedTime,
            TimeZoneInfo.Utc);

        Assert.Single(allowedResult.DueNow);
        Assert.Equal(allowedTime, allowedResult.DueNow[0].DueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T07:00:00Z"), allowedResult.NextUtc);
    }

    [Fact]
    public void Quiet_hours_fall_back_boundary_uses_the_earlier_UTC_instant()
    {
        var result = QuietHoursPolicy.NextAllowedUtc(
            DateTimeOffset.Parse("2026-11-01T07:30:00Z"),
            new QuietHours(true, new TimeOnly(23, 0), new TimeOnly(1, 0)),
            FindPacificTimeZone());

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T08:00:00Z"), result);
    }

    [Fact]
    public void Save_boundary_rejects_an_empty_weekday_set()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build() with
        {
            Rule = new RecurrenceRule.SelectedWeekdays(
                Array.Empty<DayOfWeek>(),
                new TimeOnly(9, 0)),
        };

        Assert.Throws<ArgumentException>(() => ReminderScheduler.ValidateForSave(reminder));
    }

    [Fact]
    public void Save_boundary_rejects_a_min_value_due_time()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build() with
        {
            NextDueUtc = DateTimeOffset.MinValue,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderScheduler.ValidateForSave(reminder));
    }

    [Fact]
    public void Save_boundary_rejects_a_missing_due_time_on_enabled_recurring_reminders()
    {
        var recurring = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build() with { NextDueUtc = null };

        Assert.Throws<ArgumentException>(() => ReminderScheduler.ValidateForSave(recurring));

        var disabled = recurring with { Enabled = false };
        ReminderScheduler.ValidateForSave(disabled);

        var completedOnce = new Reminder(
            "completed", "Completed", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly, null);
        ReminderScheduler.ValidateForSave(completedOnce);
    }

    [Fact]
    public void Reconcile_reports_a_collapsed_window_through_the_collapse_hook()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build();
        var collapses = new List<string>();

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
            TimeZoneInfo.Utc,
            collapses.Add);

        Assert.Single(result.DueNow);
        var collapse = Assert.Single(collapses);
        Assert.Contains("6 occurrences", collapse, StringComparison.Ordinal);
        Assert.Contains(reminder.Id, collapse, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_stays_silent_when_nothing_collapses()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build();
        var collapses = new List<string>();

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
            TimeZoneInfo.Utc,
            collapses.Add);

        Assert.Single(result.DueNow);
        Assert.Empty(collapses);
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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quiet_deferral_preserves_a_once_occurrence_until_delivery(bool snoozed)
    {
        var due = DateTimeOffset.Parse("2026-09-11T23:00:00Z");
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build() with
        {
            Rule = new RecurrenceRule.Once(),
            SnoozedUntilUtc = snoozed ? due : null,
        };
        var expected = DateTimeOffset.Parse("2026-09-12T07:00:00Z");

        var deferred = ReminderScheduler.Reconcile(reminder, due, due, TimeZoneInfo.Utc);

        Assert.Empty(deferred.DueNow);
        Assert.Equal(expected, deferred.NextUtc);
        if (!snoozed)
        {
            Assert.Equal(expected, ReminderScheduler.NextOccurrence(reminder, due, TimeZoneInfo.Utc));
        }
        reminder = reminder with { NextDueUtc = deferred.NextUtc, SnoozedUntilUtc = null };
        var delivered = ReminderScheduler.Reconcile(reminder, expected, expected, TimeZoneInfo.Utc);
        Assert.Equal(expected, Assert.Single(delivered.DueNow).DueUtc);
        Assert.Null(delivered.NextUtc);
        reminder = reminder with { NextDueUtc = delivered.NextUtc };
        Assert.Empty(ReminderScheduler.Reconcile(reminder, expected, expected.AddMinutes(1), TimeZoneInfo.Utc).DueNow);
    }

    [Fact]
    public void Multi_day_catch_up_keeps_the_latest_quiet_deferral()
    {
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build();
        var expected = DateTimeOffset.Parse("2026-09-13T07:00:00Z");
        var result = ReminderScheduler.Reconcile(reminder, reminder.NextDueUtc!.Value,
            DateTimeOffset.Parse("2026-09-12T23:30:00Z"), TimeZoneInfo.Utc);

        Assert.Empty(result.DueNow);
        Assert.Equal(expected, result.NextUtc);
        reminder = reminder with { NextDueUtc = result.NextUtc };
        var delivered = ReminderScheduler.Reconcile(reminder, expected, expected, TimeZoneInfo.Utc);
        Assert.Equal(expected, Assert.Single(delivered.DueNow).DueUtc);
        Assert.Equal(expected.AddDays(1), delivered.NextUtc);
    }

    [Theory]
    [InlineData("once")]
    [InlineData("daily")]
    [InlineData("interval")]
    public void Catch_up_waits_when_the_actual_delivery_time_is_quiet(string rule)
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build() with { Rule = RuleForTest(rule) };
        var expected = DateTimeOffset.Parse("2026-09-12T07:00:00Z");
        var result = ReminderScheduler.Reconcile(reminder, reminder.NextDueUtc!.Value,
            DateTimeOffset.Parse("2026-09-11T23:00:00Z"), TimeZoneInfo.Utc);

        Assert.Empty(result.DueNow);
        Assert.Equal(expected, result.NextUtc);
        reminder = reminder with { NextDueUtc = result.NextUtc };
        var delivered = ReminderScheduler.Reconcile(reminder, expected, expected, TimeZoneInfo.Utc);
        Assert.Equal(expected, Assert.Single(delivered.DueNow).DueUtc);
        Assert.True(delivered.NextUtc is null || delivered.NextUtc > expected);
    }

    [Theory]
    [InlineData("once", 0, true)]
    [InlineData("daily", 0, true)]
    [InlineData("interval", 0, true)]
    [InlineData("once", 30, true)]
    [InlineData("daily", 60, true)]
    [InlineData("interval", 61, false)]
    [InlineData("once", 3600, false)]
    [InlineData("daily", 3600, false)]
    [InlineData("interval", 3600, false)]
    public void Skip_distinguishes_on_time_ticks_from_missed_occurrences(string rule, int secondsLate, bool delivers)
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build() with
        {
            Rule = RuleForTest(rule),
            MissedPolicy = MissedOccurrencePolicy.Skip,
        };
        var due = reminder.NextDueUtc!.Value;
        var result = ReminderScheduler.Reconcile(reminder, due, due.AddSeconds(secondsLate), TimeZoneInfo.Utc);

        Assert.Equal(delivers ? 1 : 0, result.DueNow.Count);
        if (delivers)
        {
            Assert.Equal(due, result.DueNow[0].DueUtc);
        }
        Assert.True(result.NextUtc is null || result.NextUtc > due.AddSeconds(secondsLate));
    }

    [Fact]
    public void Skip_delivers_a_current_interval_after_skipping_older_intervals()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2))
            .WithMissedPolicy(MissedOccurrencePolicy.Skip).Build();
        var now = DateTimeOffset.Parse("2026-09-11T18:00:00Z");
        var result = ReminderScheduler.Reconcile(reminder, reminder.NextDueUtc!.Value, now, TimeZoneInfo.Utc);

        Assert.Equal(now, Assert.Single(result.DueNow).DueUtc);
        Assert.Equal(now.AddHours(2), result.NextUtc);
    }

    [Fact]
    public void Skip_does_not_resurrect_a_missed_occurrence_because_delivery_is_quiet()
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .WithMissedPolicy(MissedOccurrencePolicy.Skip).Build();
        var result = ReminderScheduler.Reconcile(reminder, reminder.NextDueUtc!.Value,
            DateTimeOffset.Parse("2026-09-11T23:00:00Z"), TimeZoneInfo.Utc);

        Assert.Empty(result.DueNow);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), result.NextUtc);
    }

    [Fact]
    public void Skip_keeps_an_on_time_occurrence_deferred_by_quiet_hours()
    {
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .WithMissedPolicy(MissedOccurrencePolicy.Skip).Build();
        var due = reminder.NextDueUtc!.Value;
        var deferred = ReminderScheduler.Reconcile(reminder, due, due, TimeZoneInfo.Utc);
        Assert.Empty(deferred.DueNow);
        Assert.Equal(due.AddHours(8), deferred.NextUtc);
        reminder = reminder with { NextDueUtc = deferred.NextUtc };
        var delivered = ReminderScheduler.Reconcile(reminder, deferred.NextUtc!.Value,
            deferred.NextUtc.Value.AddSeconds(30), TimeZoneInfo.Utc);
        Assert.Equal(deferred.NextUtc, Assert.Single(delivered.DueNow).DueUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Save_boundary_rejects_nonpositive_intervals(int seconds)
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromSeconds(seconds)).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderScheduler.ValidateForSave(reminder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_boundary_rejects_undefined_weekdays_even_when_mixed_with_valid_days(bool mixed)
    {
        var days = new HashSet<DayOfWeek> { (DayOfWeek)7 };
        if (mixed)
        {
            days.Add(DayOfWeek.Monday);
        }
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build() with
        {
            Rule = new RecurrenceRule.SelectedWeekdays(days, new TimeOnly(9, 0)),
        };
        Assert.Throws<ArgumentException>(() => ReminderScheduler.ValidateForSave(reminder));
    }

    [Fact]
    public void Save_boundary_accepts_valid_intervals_and_weekdays()
    {
        ReminderScheduler.ValidateForSave(ReminderBuilder.Every(TimeSpan.FromSeconds(1)).Build());
        ReminderScheduler.ValidateForSave(ReminderBuilder.AtLocalTime(9, 0).On(DayOfWeek.Monday).Build());
    }

    [Fact]
    public void Snoozing_a_not_yet_due_daily_reminder_does_not_lose_the_pending_occurrence()
    {
        // Audit regression: a snooze set while the reminder was not yet due
        // (SnoozedUntilUtc earlier than NextDueUtc) used to make the reconciler
        // skip NextDueUtc's own candidate once the window reached it, silently
        // dropping that legitimate occurrence in favor of the premature snooze.
        var reminder = ReminderBuilder.AtLocalTime(21, 0).Build() with
        {
            NextDueUtc = DateTimeOffset.Parse("2026-09-12T21:00:00Z"),
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-11T14:15:00Z"),
        };

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T13:55:00Z"),
            DateTimeOffset.Parse("2026-09-12T21:30:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T21:00:00Z"), Assert.Single(result.DueNow).DueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T21:00:00Z"), result.NextUtc);
    }

    [Fact]
    public void Snoozing_a_not_yet_due_interval_reminder_does_not_lose_the_pending_occurrence()
    {
        var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2)).Build() with
        {
            NextDueUtc = DateTimeOffset.Parse("2026-09-11T20:00:00Z"),
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-11T10:15:00Z"),
        };

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T20:30:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T20:00:00Z"), Assert.Single(result.DueNow).DueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T22:00:00Z"), result.NextUtc);
    }

    [Fact]
    public void Snoozing_a_not_yet_due_once_reminder_does_not_lose_its_only_occurrence()
    {
        var reminder = new Reminder(
            "once-reminder", "Once", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly,
            NextDueUtc: DateTimeOffset.Parse("2026-09-12T09:00:00Z"),
            SnoozedUntilUtc: DateTimeOffset.Parse("2026-09-11T14:15:00Z"));

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T13:55:00Z"),
            DateTimeOffset.Parse("2026-09-12T09:30:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), Assert.Single(result.DueNow).DueUtc);
        Assert.Null(result.NextUtc);
    }

    [Fact]
    public void A_live_snooze_of_an_already_due_reminder_still_suppresses_the_double_fire()
    {
        // Guards against over-correcting: when the reminder really was due at
        // snooze time (NextDueUtc <= SnoozedUntilUtc), the snoozed instant is
        // still the one and only delivery -- NextDueUtc's own candidate must
        // stay suppressed so the reminder does not fire twice.
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build() with
        {
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-11T09:15:00Z"),
        };

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T09:20:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T09:15:00Z"), Assert.Single(result.DueNow).DueUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), result.NextUtc);
    }

    [Fact]
    public void A_dead_snooze_does_not_swallow_an_off_grid_persisted_deferral()
    {
        // Opus review regression on top of finding 1: the base arm that counts a
        // persisted, off-recurrence-grid NextDueUtc (e.g. a quiet-hours deferral)
        // was still gated on "snoozedUntilUtc is null", not on isLiveSnooze. A
        // dead snooze (SnoozedUntilUtc earlier than NextDueUtc) therefore blocked
        // that base arm even though it does not stand in for NextDueUtc, and the
        // daily rule arm never finds an off-grid time either -- so the reminder
        // silently vanished. Scenario: daily 23:00, quiet hours 22:00-07:00
        // WaitUntilQuietHoursEnd defer NextDueUtc to 07:00; snoozing at 06:00 for
        // 15 min (SnoozedUntil 06:15 < NextDueUtc 07:00) is a dead snooze. The
        // window here starts after that dead snooze has already passed, so only
        // the base arm can still deliver the real 07:00 occurrence.
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build() with
        {
            NextDueUtc = DateTimeOffset.Parse("2026-09-12T07:00:00Z"),
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T06:15:00Z"),
        };

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-12T06:20:00Z"),
            DateTimeOffset.Parse("2026-09-12T07:00:30Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T07:00:00Z"), Assert.Single(result.DueNow).DueUtc);
    }

    [Fact]
    public void A_snooze_exactly_at_next_due_is_a_live_snooze_that_fires_once()
    {
        // Boundary: SnoozedUntilUtc == NextDueUtc makes isLiveSnooze true
        // (nextDueUtc <= snoozedUntilUtc), which suppresses the base arm
        // entirely -- so it is the snooze arm, not the base arm, that supplies
        // this occurrence. It must still fire exactly once: the daily arm's
        // own guards (skipping candidate == nextDueUtc under a live snooze,
        // and candidate == snoozeCounted regardless) must stop it from being
        // double-counted against that same 09:00 instant.
        var reminder = ReminderBuilder.AtLocalTime(9, 0)
            .SnoozedUntil(DateTimeOffset.Parse("2026-09-11T09:00:00Z"))
            .Build();

        var result = ReminderScheduler.Reconcile(
            reminder,
            DateTimeOffset.Parse("2026-09-11T08:50:00Z"),
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T09:00:00Z"), Assert.Single(result.DueNow).DueUtc);
    }

    [Fact]
    public void Completing_a_not_yet_due_once_reminder_consumes_its_pending_occurrence()
    {
        // Audit regression: completing used to call NextOccurrence with the
        // real "now", which for a not-yet-due reminder just returns NextDueUtc
        // unchanged -- so the toast still fired later even though "done" was
        // pressed.
        var reminder = new Reminder(
            "once", "Once", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-12T09:00:00Z"));

        var next = ReminderScheduler.NextOccurrenceAfterCompletion(
            reminder,
            DateTimeOffset.Parse("2026-09-11T15:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Null(next);
    }

    [Fact]
    public void Completing_a_not_yet_due_daily_reminder_advances_past_todays_occurrence()
    {
        var reminder = ReminderBuilder.AtLocalTime(21, 0).Build();

        var next = ReminderScheduler.NextOccurrenceAfterCompletion(
            reminder,
            DateTimeOffset.Parse("2026-09-11T15:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T21:00:00Z"), next);
    }

    [Fact]
    public void Completing_an_already_due_reminder_matches_plain_next_occurrence()
    {
        var reminder = ReminderBuilder.AtLocalTime(9, 0).Build();
        var now = DateTimeOffset.Parse("2026-09-11T10:00:00Z");

        var expected = ReminderScheduler.NextOccurrence(reminder, now, TimeZoneInfo.Utc);
        var actual = ReminderScheduler.NextOccurrenceAfterCompletion(reminder, now, TimeZoneInfo.Utc);

        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:00:00Z"), actual);
    }

    [Fact]
    public void Completing_a_daily_reminder_whose_pending_occurrence_is_inside_quiet_hours_advances_to_the_next_day()
    {
        // Audit regression: evaluating from max(now, NextDueUtc) landed exactly on
        // the still-quiet pending instant, so NextOccurrence's own quiet-hours
        // deferral arm handed back that SAME occurrence merely pushed to the end
        // of quiet hours -- a completed reminder kept firing. Completion must
        // consume the pending occurrence (deferred, if it was quiet) and resolve
        // the *next* one from there.
        var reminder = ReminderBuilder.AtLocalTime(23, 0)
            .WithQuietHours(new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .Build();

        var next = ReminderScheduler.NextOccurrenceAfterCompletion(
            reminder,
            DateTimeOffset.Parse("2026-09-11T15:00:00Z"),
            TimeZoneInfo.Utc);

        // Today's 23:00 occurrence is consumed; tomorrow's 23:00 occurrence is
        // itself quiet and defers to the following morning.
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T07:00:00Z"), next);
    }

    [Fact]
    public void Completing_a_once_reminder_whose_pending_occurrence_is_inside_quiet_hours_has_no_next_occurrence()
    {
        var reminder = new Reminder(
            "once", "Once", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.WaitUntilQuietHoursEnd, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-11T23:00:00Z"),
            null,
            new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)));

        var next = ReminderScheduler.NextOccurrenceAfterCompletion(
            reminder,
            DateTimeOffset.Parse("2026-09-11T15:00:00Z"),
            TimeZoneInfo.Utc);

        Assert.Null(next);
    }

    private static RecurrenceRule RuleForTest(string rule) => rule switch
    {
        "once" => new RecurrenceRule.Once(),
        "daily" => new RecurrenceRule.Daily(new TimeOnly(9, 0)),
        "interval" => new RecurrenceRule.Interval(TimeSpan.FromHours(2)),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

}
