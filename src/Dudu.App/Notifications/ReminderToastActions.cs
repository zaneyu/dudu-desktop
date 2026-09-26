using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;
using Dudu.Core.Time;

namespace Dudu.App.Notifications;

/// <summary>
/// Carries out a reminder toast's Done/Snooze buttons directly, the same way
/// the Reminders page's own buttons do (<c>RemindersViewModel.CompleteAsync</c>
/// / <c>SnoozeAsync</c>): the buttons used to only open the Reminders page and
/// leave the reminder exactly as it was. The durable write is the real
/// action; the toast removal and held-copy discard after it are best-effort
/// cleanup that never turns a completed action into a
/// failure. Nothing here logs reminder titles or ids -- failures report the
/// operation name and exception only.
/// </summary>
public sealed class ReminderToastActions
{
    /// <summary>Matches the Reminders page's snooze.</summary>
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(15);

    private readonly IClock _clock;
    private readonly IReminderRepository _reminders;
    private readonly IReminderWriter _writer;
    private readonly Func<string, CancellationToken, Task> _dismissNotificationAsync;
    private readonly Func<string, CancellationToken, Task> _discardHeldAsync;
    private readonly IAppHostErrorReporter? _errorReporter;

    public ReminderToastActions(
        IClock clock,
        IReminderRepository reminders,
        IReminderWriter writer,
        Func<string, CancellationToken, Task> dismissNotificationAsync,
        Func<string, CancellationToken, Task> discardHeldAsync,
        IAppHostErrorReporter? errorReporter = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _dismissNotificationAsync = dismissNotificationAsync
            ?? throw new ArgumentNullException(nameof(dismissNotificationAsync));
        _discardHeldAsync = discardHeldAsync ?? throw new ArgumentNullException(nameof(discardHeldAsync));
        _errorReporter = errorReporter;
    }

    /// <summary>
    /// Completes the pending occurrence (as the Reminders page's Done does).
    /// Returns false when the reminder no longer exists or changed before the
    /// write committed -- the caller then opens the Reminders page instead.
    /// </summary>
    public async Task<bool> CompleteAsync(string reminderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        var reminder = await FindAsync(reminderId, cancellationToken);
        if (reminder is null)
        {
            await BestEffortAsync(_dismissNotificationAsync, reminderId, "reminder-toast-dismiss", cancellationToken);
            return false;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var next = ReminderScheduler.NextOccurrenceAfterCompletion(
            reminder,
            now,
            ResolveTimeZone(reminder.LocalTimeZoneId));
        if (!await _reminders.RecordOccurrencesAndAdvanceAsync(
                reminder,
                [new ReminderOccurrence(reminder.Id, now)],
                next,
                cancellationToken))
        {
            return false;
        }

        await BestEffortAsync(_dismissNotificationAsync, reminder.Id, "reminder-toast-dismiss", cancellationToken);
        await BestEffortAsync(_discardHeldAsync, reminder.Id, "reminder-toast-discard-held", cancellationToken);
        return true;
    }

    /// <summary>
    /// Snoozes the reminder for <see cref="SnoozeDuration"/> (as the
    /// Reminders page's Snooze does). The snooze is now always re-delivered
    /// by <see cref="ReminderEngine"/>, so any copy of this reminder still
    /// held for quiet hours/fullscreen is discarded -- the snooze replaces
    /// it. Returns false when the reminder no longer exists.
    /// </summary>
    public async Task<bool> SnoozeAsync(string reminderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        var reminder = await FindAsync(reminderId, cancellationToken);
        if (reminder is null || !reminder.Enabled)
        {
            await BestEffortAsync(_dismissNotificationAsync, reminderId, "reminder-toast-dismiss", cancellationToken);
            return false;
        }

        var snoozed = reminder with
        {
            SnoozedUntilUtc = _clock.UtcNow.ToUniversalTime() + SnoozeDuration,
        };
        await _writer.SaveAsync(snoozed, cancellationToken);
        await BestEffortAsync(_dismissNotificationAsync, reminder.Id, "reminder-toast-dismiss", cancellationToken);
        await BestEffortAsync(_discardHeldAsync, reminder.Id, "reminder-toast-discard-held", cancellationToken);
        return true;
    }

    private async Task<Reminder?> FindAsync(string reminderId, CancellationToken cancellationToken) =>
        (await _reminders.ListAsync(cancellationToken)).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, reminderId, StringComparison.Ordinal));

    private async Task BestEffortAsync(
        Func<string, CancellationToken, Task> action,
        string reminderId,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(reminderId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(operation, exception);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
            or InvalidTimeZoneException
            or ArgumentException)
        {
            // Mirror ReminderEngine: an unknown zone id falls back to UTC
            // rather than failing the action.
            return TimeZoneInfo.Utc;
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
