using Dudu.Core.Abstractions;
using Dudu.Core.Time;

namespace Dudu.Core.Reminders;

public sealed class ReminderEngine
{
    private readonly IClock _clock;
    private readonly IReminderRepository _repository;
    private readonly IReminderDueSink _dueSink;

    public ReminderEngine(
        IClock clock,
        IReminderRepository repository,
        IReminderDueSink dueSink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dueSink = dueSink ?? throw new ArgumentNullException(nameof(dueSink));
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow.ToUniversalTime();
        var reminders = await _repository.LoadDueAsync(nowUtc, cancellationToken);

        foreach (var reminder in reminders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeZone = ResolveTimeZone(reminder.LocalTimeZoneId);
            var reconciliation = ReminderScheduler.Reconcile(
                reminder,
                reminder.NextDueUtc,
                nowUtc,
                timeZone);

            // The repository call is the transaction boundary. Presentation is
            // deliberately after it so a failed write cannot produce a due event.
            await _repository.RecordOccurrencesAndAdvanceAsync(
                reminder,
                reconciliation.DueNow,
                reconciliation.NextUtc,
                cancellationToken);

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
