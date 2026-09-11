using Dudu.Core.Abstractions;
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

        public CancellationToken LoadToken { get; private set; }

        public CancellationToken RecordToken { get; private set; }

        public IReadOnlyList<ReminderOccurrence> RecordedOccurrences { get; private set; } = [];

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
        {
            LoadToken = cancellationToken;
            events.Add("load");
            return Task.FromResult(reminders);
        }

        public Task RecordOccurrencesAndAdvanceAsync(
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
