using Dudu.Core.Abstractions;
using Dudu.Core.Focus;
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

            // A snooze picked after the reminder already fired: the engine
            // advanced NextDueUtc past it (or cleared it, for a fired Once),
            // so it is no longer inside the regular Reconcile window. It is
            // delivered on its own below, keeping NextDueUtc for the regular
            // schedule.
            var snoozeOnly = reminder.SnoozedUntilUtc is { } snoozedUntilUtc
                && snoozedUntilUtc.ToUniversalTime() <= nowUtc
                && (reminder.NextDueUtc is not { } pendingUtc || pendingUtc.ToUniversalTime() > nowUtc);
            if (reminder.NextDueUtc is null && !snoozeOnly)
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
                if (snoozeOnly)
                {
                    var snooze = ReminderScheduler.ReconcileDueSnooze(reminder, nowUtc, timeZone);
                    if (snooze is null)
                    {
                        // Still held by the reminder's own quiet hours: leave
                        // the snooze untouched and retry on a later tick.
                        continue;
                    }

                    reconciliation = snooze;
                }
                else
                {
                    reconciliation = ReminderScheduler.Reconcile(
                        reminder,
                        reminder.NextDueUtc!.Value,
                        nowUtc,
                        timeZone);
                }
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
                await _dueSink.NotifyAsync(occurrence, cancellationToken);
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
