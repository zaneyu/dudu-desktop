using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Reminders;

/// <summary>
/// ReminderEngine.OccurrenceDelivered lets the app tell an announced
/// occurrence (whose row already points at the NEXT one) apart from a
/// not-yet-due one, so the Reminders page's Done no longer consumes the next
/// occurrence after a reminder fired.
/// </summary>
public sealed class ReminderEngineOccurrenceDeliveredTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T09:00:00Z");

    [Fact]
    public async Task Raised_after_the_advance_commits_and_before_the_sink_is_notified()
    {
        var events = new List<string>();
        var repository = new Repository(events, advanceResult: true);
        var sink = new Sink(events);
        var engine = new ReminderEngine(new Clock(), repository, sink);
        var delivered = new List<ReminderOccurrence>();
        engine.OccurrenceDelivered += occurrence =>
        {
            events.Add("delivered");
            delivered.Add(occurrence);
        };

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["record", "delivered", "notify"], events);
        Assert.Equal([new ReminderOccurrence("due", Now)], delivered);
    }

    [Fact]
    public async Task Not_raised_when_a_concurrent_edit_wins_the_advance()
    {
        var events = new List<string>();
        var engine = new ReminderEngine(new Clock(), new Repository(events, advanceResult: false), new Sink(events));
        var raised = false;
        engine.OccurrenceDelivered += _ => raised = true;

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(raised);
    }

    [Fact]
    public async Task A_throwing_observer_never_suppresses_the_notification_or_other_observers()
    {
        var events = new List<string>();
        var sink = new Sink(events);
        var engine = new ReminderEngine(new Clock(), new Repository(events, advanceResult: true), sink);
        var second = false;
        engine.OccurrenceDelivered += _ => throw new InvalidOperationException("observer bug");
        engine.OccurrenceDelivered += _ => second = true;

        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(second);
        Assert.Contains("notify", events);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Repository(List<string> events, bool advanceResult) : IReminderRepository
    {
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([
                new Reminder(
                    "due",
                    "Stretch",
                    null,
                    true,
                    new RecurrenceRule.Daily(new TimeOnly(9, 0)),
                    "UTC",
                    QuietHoursBehavior.DeliverImmediately,
                    MissedOccurrencePolicy.LatestOnly,
                    Now),
            ]);

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            events.Add("record");
            return Task.FromResult(advanceResult);
        }
    }

    private sealed class Sink(List<string> events) : IReminderDueSink
    {
        public Task NotifyAsync(ReminderOccurrence occurrence, CancellationToken cancellationToken)
        {
            events.Add("notify");
            return Task.CompletedTask;
        }
    }
}
