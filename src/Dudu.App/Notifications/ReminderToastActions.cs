using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Pet;
using Dudu.Core.Time;

namespace Dudu.App.Notifications;

/// <summary>
/// Carries out a reminder toast's Done and Snooze buttons. They used to only
/// open the Reminders page and drop the toast, so the reminder was neither
/// acknowledged nor brought back later.
/// <para>
/// A reminder toast is only ever raised after <c>ReminderEngine</c> has
/// recorded the occurrence and advanced <c>NextDueUtc</c> past it, so the row
/// these buttons see already points at the NEXT occurrence:
/// </para>
/// <list type="bullet">
/// <item>Done acknowledges the announced occurrence (clears it from the pet,
/// the toast, and any held copy). It must not advance the schedule again --
/// doing so would also consume the next, not-yet-due occurrence (an hourly
/// reminder would silently skip an hour).</item>
/// <item>Snooze brings the announced occurrence back in
/// <see cref="SnoozeDuration"/> by making it the pending occurrence again
/// (<c>NextDueUtc</c> = now + 15 min, like a quiet-hours deferral), through
/// the repository's compare-and-set so a concurrent edit or engine tick is
/// never overwritten. If the next real occurrence already falls inside the
/// snooze window nothing is rescheduled -- it comes back by then anyway.</item>
/// </list>
/// Failures are reported as <see cref="Operation"/> with the exception only
/// (never the reminder's title or details) and return false so the caller can
/// fall back to opening the Reminders page.
/// </summary>
public sealed class ReminderToastActions
{
    public const string Operation = "reminder-toast-action";
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(15);
    internal const int MaxSnoozeAttempts = 3;

    private readonly IClock _clock;
    private readonly IReminderRepository _reminders;
    private readonly Func<string, CancellationToken, Task> _dismissNotificationAsync;
    private readonly Func<string, CancellationToken, Task> _discardHeldAsync;
    private readonly Func<PetEvent, CancellationToken, Task>? _presentPetAsync;
    private readonly IAppHostErrorReporter? _errorReporter;

    public ReminderToastActions(
        IClock clock,
        IReminderRepository reminders,
        Func<string, CancellationToken, Task> dismissNotificationAsync,
        Func<string, CancellationToken, Task> discardHeldAsync,
        Func<PetEvent, CancellationToken, Task>? presentPetAsync = null,
        IAppHostErrorReporter? errorReporter = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _dismissNotificationAsync = dismissNotificationAsync
            ?? throw new ArgumentNullException(nameof(dismissNotificationAsync));
        _discardHeldAsync = discardHeldAsync ?? throw new ArgumentNullException(nameof(discardHeldAsync));
        _presentPetAsync = presentPetAsync;
        _errorReporter = errorReporter;
    }

    /// <summary>Done: acknowledges the occurrence the toast announced.
    /// Always succeeds; each cleanup step is best-effort.</summary>
    public async Task<bool> CompleteAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        await AcknowledgeAsync(reminderId, cancellationToken);
        return true;
    }

    /// <summary>Snooze: brings the announced occurrence back in 15 minutes.
    /// Returns false (after reporting) when the reschedule could not be
    /// saved; the toast is then left in Action Center.</summary>
    public async Task<bool> SnoozeAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        try
        {
            await RescheduleAsync(reminderId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(exception);
            return false;
        }

        await AcknowledgeAsync(reminderId, cancellationToken);
        return true;
    }

    private async Task RescheduleAsync(string reminderId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxSnoozeAttempts; attempt++)
        {
            var reminder = (await _reminders.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, reminderId, StringComparison.Ordinal));
            if (reminder is null || !reminder.Enabled)
            {
                // Deleted or switched off since the toast went up: there is
                // nothing to bring back, and a snooze must not re-enable it.
                return;
            }

            var now = _clock.UtcNow.ToUniversalTime();
            var until = now + SnoozeDuration;
            if (reminder.NextDueUtc is { } next
                && next.ToUniversalTime() > now
                && next.ToUniversalTime() <= until)
            {
                return;
            }

            if (await _reminders.RecordOccurrencesAndAdvanceAsync(reminder, [], until, cancellationToken))
            {
                return;
            }

            // Compare-and-set lost to a concurrent edit or engine tick; re-read
            // and decide again against the row that won.
        }

        throw new InvalidOperationException("The reminder kept changing while it was being snoozed.");
    }

    private async Task AcknowledgeAsync(string reminderId, CancellationToken cancellationToken)
    {
        if (_presentPetAsync is not null)
        {
            await BestEffortAsync(() => _presentPetAsync(new PetEvent.Dismissed(reminderId), cancellationToken), cancellationToken);
        }

        await BestEffortAsync(() => _dismissNotificationAsync(reminderId, cancellationToken), cancellationToken);
        // A copy held back by quiet hours/fullscreen/pause would otherwise
        // surface the same occurrence again after she already answered it.
        await BestEffortAsync(() => _discardHeldAsync(reminderId, cancellationToken), cancellationToken);
    }

    private async Task BestEffortAsync(Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(Operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            Operation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
