using Dudu.Core.Abstractions;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.Core.Reminders;

public sealed class ReminderEngine
{
    private readonly IClock _clock;
    private readonly IReminderRepository _repository;
    private readonly IReminderDueSink _dueSink;
    private readonly FocusService? _focusService;

    public ReminderEngine(
        IClock clock,
        IReminderRepository repository,
        IReminderDueSink dueSink,
        FocusService? focusService = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dueSink = dueSink ?? throw new ArgumentNullException(nameof(dueSink));
        _focusService = focusService;
    }

    /// <summary>
    /// Raised for each occurrence the tick delivers, after the advance past it
    /// has committed and just before it is handed to the due sink. Once that
    /// commit lands, the reminder row already points at the NEXT occurrence,
    /// so the row alone can no longer tell "announced, waiting for an answer"
    /// apart from "not due yet"; this lets the app keep that distinction (a
    /// "done" on an announced occurrence must not also consume the next one).
    /// Handlers must be quick and must not throw: an exception from one is
    /// ignored so it can never break the tick or suppress the notification.
    /// </summary>
    public event Action<ReminderOccurrence>? OccurrenceDelivered;

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow.ToUniversalTime();
        if (_focusService is not null)
        {
            await _focusService.CompleteExpiredAsync(cancellationToken);
        }

        var reminders = await _repository.LoadDueAsync(nowUtc, cancellationToken);

        foreach (var reminder in reminders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reminder.NextDueUtc is null)
            {
                continue;
            }

            TimeZoneInfo timeZone;
            try
            {
                timeZone = ResolveTimeZone(reminder.LocalTimeZoneId);
            }
            catch (Exception exception)
                when (exception is TimeZoneNotFoundException
                    or InvalidTimeZoneException
                    or ArgumentException)
            {
                // A corrupt or unknown zone id must not wedge the whole tick.
                // Fall back to UTC for this reminder and keep processing the rest.
                timeZone = TimeZoneInfo.Utc;
            }

            ReminderReconciliation reconciliation;
            try
            {
                reconciliation = ReminderScheduler.Reconcile(
                    reminder,
                    reminder.NextDueUtc.Value,
                    nowUtc,
                    timeZone);
            }
            catch (ArgumentException)
            {
                // A corrupt schedule (e.g. a non-positive interval) throws on every
                // tick. Skip only that reminder so its neighbors keep firing.
                continue;
            }

            // The repository call is the transaction boundary. Presentation is
            // deliberately after it so a failed write cannot produce a due event.
            // Delivery is therefore at-most-once: if NotifyAsync fails or the app
            // dies after the advance commits, that occurrence is not retried.
            var advanced = await _repository.RecordOccurrencesAndAdvanceAsync(
                reminder,
                reconciliation.DueNow,
                reconciliation.NextUtc,
                cancellationToken);

            if (!advanced)
            {
                // A settings edit or snooze won the race after LoadDueAsync.
                // Its durable state must remain authoritative and must not
                // produce an event based on this stale snapshot.
                continue;
            }

            foreach (var occurrence in reconciliation.DueNow)
            {
                RaiseOccurrenceDelivered(occurrence);
                await _dueSink.NotifyAsync(occurrence, cancellationToken);
            }
        }
    }

    private void RaiseOccurrenceDelivered(ReminderOccurrence occurrence)
    {
        if (OccurrenceDelivered is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ReminderOccurrence>)handler)(occurrence);
            }
            catch
            {
                // See OccurrenceDelivered: observers are advisory only.
            }
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrEmpty(timeZoneId);

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }
    }
}
