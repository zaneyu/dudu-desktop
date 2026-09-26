using Dudu.Core.Abstractions;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Reminders;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Reminders;

public sealed class ReminderEngineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T09:00:00Z");

    [Fact]
    public async Task Tick_advances_repository_before_notifying_sink()
    {
        var events = new List<string>();
        var repository = new FakeReminderRepository([DueReminder()], events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["load", "record", "notify"], events);
        Assert.Single(sink.Notifications);
        Assert.Equal(repository.RecordedOccurrences.Single().ReminderId, sink.Notifications[0].ReminderId);
    }

    [Fact]
    public async Task Tick_does_not_notify_when_repository_transaction_fails()
    {
        var events = new List<string>();
        var repository = new FakeReminderRepository([DueReminder()], events)
        {
            FailRecord = true,
        };
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.TickAsync(TestContext.Current.CancellationToken));

        Assert.Empty(sink.Notifications);
        Assert.DoesNotContain("notify", events);
    }

    [Fact]
    public async Task Tick_passes_the_cancellation_token_to_repository_and_sink_boundaries()
    {
        var events = new List<string>();
        var token = new CancellationTokenSource().Token;
        var repository = new FakeReminderRepository([DueReminder()], events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await engine.TickAsync(token);

        Assert.Equal(token, repository.LoadToken);
        Assert.Equal(token, repository.RecordToken);
        Assert.Equal(token, sink.NotifyToken);
    }

    [Fact]
    public async Task Tick_propagates_repository_cancellation_without_notifying()
    {
        var events = new List<string>();
        var cancellation = new CancellationTokenSource();
        var repository = new FakeReminderRepository([DueReminder()], events)
        {
            CancelRecord = true,
        };
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.TickAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, repository.RecordToken);
        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public async Task Tick_propagates_sink_cancellation_after_repository_commit()
    {
        var events = new List<string>();
        var cancellation = new CancellationTokenSource();
        var repository = new FakeReminderRepository([DueReminder()], events);
        var sink = new FakeReminderDueSink(events)
        {
            CancelNotify = true,
        };
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.TickAsync(cancellation.Token));

        Assert.Equal(["load", "record", "notify"], events);
        Assert.Equal(cancellation.Token, sink.NotifyToken);
        Assert.Single(repository.RecordedOccurrences);
    }

    [Fact]
    public async Task Tick_does_not_notify_when_a_concurrent_edit_wins_the_advance_race()
    {
        var events = new List<string>();
        var repository = new FakeReminderRepository([DueReminder()], events)
        {
            AdvanceResult = false,
        };
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public async Task Tick_reconciles_an_expired_focus_session()
    {
        var focusRepository = new FakeFocusSessionRepository();
        var expired = new FocusSession(
            Guid.NewGuid(), null, Now.AddMinutes(-25), Now, TimeSpan.Zero,
            FocusStatus.Running, Now.AddMinutes(-25));
        await focusRepository.SaveAsync(expired, TestContext.Current.CancellationToken);
        var engine = new ReminderEngine(
            new FakeClock(Now),
            new FakeReminderRepository([], []),
            new FakeReminderDueSink([]),
            new FocusService(focusRepository, new FakeClock(Now)));

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FocusStatus.Completed, (await focusRepository.GetAsync(
            expired.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tick_persists_quiet_deferral_without_notifying(bool once)
    {
        var now = Now.AddHours(14);
        var reminder = DueReminder() with
        {
            Rule = once ? new RecurrenceRule.Once() : new RecurrenceRule.Daily(new TimeOnly(9, 0)),
            QuietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd,
            QuietHours = new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)),
        };
        var events = new List<string>();
        var repository = new FakeReminderRepository([reminder], events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(now), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["load", "record"], events);
        Assert.Empty(sink.Notifications);
        Assert.Empty(repository.RecordedOccurrences);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T07:00:00Z"), repository.NextDueUtc);
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(120, 0)]
    public async Task Tick_applies_skip_only_after_the_on_time_grace_period(int secondsLate, int expectedCount)
    {
        var reminder = DueReminder() with { MissedPolicy = MissedOccurrencePolicy.Skip };
        var repository = new FakeReminderRepository([reminder], []);
        var sink = new FakeReminderDueSink([]);
        var engine = new ReminderEngine(new FakeClock(Now.AddSeconds(secondsLate)), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedCount, sink.Notifications.Count);
        Assert.Equal(Now.AddDays(1), repository.NextDueUtc);
    }

    [Fact]
    public async Task Tick_delivers_a_snooze_picked_after_a_daily_reminder_fired_and_keeps_its_next_due()
    {
        // Regression: the engine advances NextDueUtc before notifying, so a
        // snooze she picks after the 09:00 toast sits BEFORE the new
        // NextDueUtc (tomorrow 09:00). The regular Reconcile window starts at
        // NextDueUtc and never saw it, so the snooze silently did nothing.
        var tomorrow = Now.AddDays(1);
        var snoozedUntil = Now.AddMinutes(15);
        var events = new List<string>();
        var repository = new FakeReminderRepository(
            [DueReminder() with { NextDueUtc = tomorrow, SnoozedUntilUtc = snoozedUntil }],
            events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(snoozedUntil.AddSeconds(10)), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["load", "record", "notify"], events);
        var delivered = Assert.Single(sink.Notifications);
        Assert.Equal(snoozedUntil, delivered.DueUtc);
        Assert.Equal(tomorrow, repository.NextDueUtc);
    }

    [Fact]
    public async Task Tick_delivers_a_snooze_of_a_fired_one_off_reminder()
    {
        // A fired Once reminder has NextDueUtc == null and used to be skipped
        // outright, snooze or not.
        var snoozedUntil = Now.AddMinutes(15);
        var events = new List<string>();
        var repository = new FakeReminderRepository(
            [DueReminder() with { Rule = new RecurrenceRule.Once(), NextDueUtc = null, SnoozedUntilUtc = snoozedUntil }],
            events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(snoozedUntil), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(snoozedUntil, Assert.Single(sink.Notifications).DueUtc);
        Assert.Null(repository.NextDueUtc);
        Assert.Equal(snoozedUntil, Assert.Single(repository.RecordedOccurrences).DueUtc);
    }

    [Fact]
    public async Task Tick_leaves_a_due_snooze_in_place_while_the_reminders_own_quiet_hours_hold_it()
    {
        var snoozedUntil = Now.AddMinutes(15);
        var events = new List<string>();
        var repository = new FakeReminderRepository(
            [DueReminder() with
            {
                NextDueUtc = Now.AddDays(1),
                SnoozedUntilUtc = snoozedUntil,
                QuietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd,
                QuietHours = new QuietHours(true, new TimeOnly(9, 10), new TimeOnly(10, 0)),
            }],
            events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(snoozedUntil), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["load"], events);
        Assert.Empty(sink.Notifications);
    }

    private static Reminder DueReminder()
    {
        return new Reminder(
            "engine-reminder",
            "Engine reminder",
            null,
            true,
            new RecurrenceRule.Daily(new TimeOnly(9, 0)),
            "UTC",
            QuietHoursBehavior.DeliverImmediately,
            MissedOccurrencePolicy.LatestOnly,
            Now);
    }

    [Fact]
    public async Task Tick_falls_back_to_utc_for_a_corrupt_time_zone_without_skipping_neighbors()
    {
        var events = new List<string>();
        var corrupt = DueReminder() with
        {
            Id = "corrupt-zone",
            LocalTimeZoneId = "Bogus/Not-A-Zone",
        };
        var repository = new FakeReminderRepository([corrupt, DueReminder()], events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sink.Notifications.Count);
        Assert.Contains(sink.Notifications, occurrence => occurrence.ReminderId == "corrupt-zone");
        Assert.Contains(sink.Notifications, occurrence => occurrence.ReminderId == "engine-reminder");
    }

    [Fact]
    public async Task Tick_skips_a_corrupt_schedule_without_skipping_neighbors()
    {
        var events = new List<string>();
        var corrupt = DueReminder() with
        {
            Id = "corrupt-interval",
            Rule = new RecurrenceRule.Interval(TimeSpan.Zero),
        };
        var repository = new FakeReminderRepository([corrupt, DueReminder()], events);
        var sink = new FakeReminderDueSink(events);
        var engine = new ReminderEngine(new FakeClock(Now), repository, sink);

        await engine.TickAsync(TestContext.Current.CancellationToken);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("engine-reminder", notification.ReminderId);
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeReminderRepository(
        IReadOnlyList<Reminder> reminders,
        List<string> events) : IReminderRepository
    {
        public bool FailRecord { get; init; }

        public bool CancelRecord { get; init; }

        public bool AdvanceResult { get; init; } = true;

        public CancellationToken LoadToken { get; private set; }

        public CancellationToken RecordToken { get; private set; }

        public IReadOnlyList<ReminderOccurrence> RecordedOccurrences { get; private set; } = [];

        public DateTimeOffset? NextDueUtc { get; private set; }

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
        {
            LoadToken = cancellationToken;
            events.Add("load");
            return Task.FromResult(reminders);
        }

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            RecordToken = cancellationToken;
            events.Add("record");
            if (CancelRecord)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (FailRecord)
            {
                throw new InvalidOperationException("transaction failed");
            }

            RecordedOccurrences = occurrences;
            NextDueUtc = nextDueUtc;
            return Task.FromResult(AdvanceResult);
        }
    }

    private sealed class FakeFocusSessionRepository : IFocusSessionRepository
    {
        private readonly Dictionary<Guid, FocusSession> _sessions = [];

        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.GetValueOrDefault(id));

        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.Values.FirstOrDefault(item => item.Status is FocusStatus.Running or FocusStatus.Paused));

        public Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FocusSession>>(_sessions.Values.Where(item => item.Status is not (FocusStatus.Running or FocusStatus.Paused)).ToArray());

        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            _sessions[session.Id] = session;
            return Task.FromResult(true);
        }

        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken)
        {
            if (!_sessions.TryGetValue(expected.Id, out var current) || current != expected)
            {
                return Task.FromResult(false);
            }

            _sessions[replacement.Id] = replacement;
            return Task.FromResult(true);
        }

        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReminderDueSink(List<string> events) : IReminderDueSink
    {
        public List<ReminderOccurrence> Notifications { get; } = [];

        public bool CancelNotify { get; init; }

        public CancellationToken NotifyToken { get; private set; }

        public Task NotifyAsync(
            ReminderOccurrence occurrence,
            CancellationToken cancellationToken)
        {
            NotifyToken = cancellationToken;
            events.Add("notify");
            if (CancelNotify)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Notifications.Add(occurrence);
            return Task.CompletedTask;
        }
    }
}
