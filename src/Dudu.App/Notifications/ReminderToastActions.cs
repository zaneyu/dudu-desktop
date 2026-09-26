using System.Collections.Concurrent;
using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Dudu.Core.Time;

namespace Dudu.App.Notifications;

/// <summary>What a Done or Snooze actually did to a reminder.</summary>
public enum ReminderAnswer
{
    /// <summary>Done on an occurrence that was already announced: its toast,
    /// pet bubble and held copy are cleared; the schedule is untouched.</summary>
    Acknowledged,

    /// <summary>Done ahead of time (nothing announced yet): the pending
    /// occurrence is consumed so it no longer fires.</summary>
    CompletedAhead,

    /// <summary>The occurrence comes back in
    /// <see cref="ReminderToastActions.SnoozeDuration"/>.</summary>
    Snoozed,

    /// <summary>Snooze of an announced occurrence whose next real occurrence is
    /// due within the snooze window anyway: nothing was rescheduled.</summary>
    NextComesSooner,

    /// <summary>Snooze of a reminder that has not fired and is not due within
    /// the snooze window: nothing changed.</summary>
    NotDueYet,

    /// <summary>The reminder is switched off: nothing was rescheduled.</summary>
    SwitchedOff,

    /// <summary>The reminder no longer exists.</summary>
    Missing,
}

/// <summary>The outcome of a Done/Snooze and, when the row was rewritten, the
/// row as the repository now stores it.</summary>
public sealed record ReminderActionResult(ReminderAnswer Answer, Reminder? Updated = null);

/// <summary>
/// Carries out Done and Snooze for a reminder, from a toast button or from
/// the Reminders page, so both surfaces share one set of rules. The toast
/// buttons used to only open the Reminders page, and the page's own Done and
/// Snooze disagreed with the toast (Done skipped an occurrence, Snooze was
/// ignored by the scheduler).
/// <para>
/// A reminder is only ever announced after <c>ReminderEngine</c> has recorded
/// the occurrence and advanced <c>NextDueUtc</c> past it, so once announced
/// the row already points at the NEXT occurrence. The engine reports each
/// announcement through <see cref="RecordAnnounced"/> (wired to
/// <c>ReminderEngine.OccurrenceDelivered</c>), which is how an announced,
/// still-unanswered occurrence is told apart from a not-yet-due one:
/// </para>
/// <list type="bullet">
/// <item>Done on an announced occurrence (always, for a toast) acknowledges it
/// (clears it from the pet, the toast, and any held copy). It must not
/// advance the schedule again -- doing so would also consume the next,
/// not-yet-due occurrence (an hourly reminder would silently skip an
/// hour).</item>
/// <item>Done from the page on a reminder that has not fired completes it
/// ahead of time: the pending occurrence is consumed
/// (<see cref="ReminderScheduler.NextOccurrenceAfterCompletion"/>) so it does
/// not still fire later.</item>
/// <item>Snooze brings the occurrence back in <see cref="SnoozeDuration"/> by
/// making it the pending occurrence again (<c>NextDueUtc</c> = now + 15 min,
/// like a quiet-hours deferral). For an announced occurrence nothing is
/// rescheduled when the next real occurrence already falls inside the snooze
/// window -- it comes back by then anyway. For one that has not fired, only
/// an occurrence due within the window (or overdue) can be snoozed.</item>
/// </list>
/// Every write goes through the repository's compare-and-set against a
/// freshly read row (retried a few times), so a concurrent edit or engine
/// tick is never overwritten and a stale copy on the page cannot fail with
/// "reminder changed". Cleanup failures are reported as
/// <see cref="Operation"/> (toast) or <see cref="PageOperation"/> (page) with
/// the exception only (never the reminder's title or details).
/// </summary>
public sealed class ReminderToastActions
{
    public const string Operation = "reminder-toast-action";
    public const string PageOperation = "reminder-page-action";
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(15);
    internal const int MaxAttempts = 3;
    internal const int MaxSnoozeAttempts = MaxAttempts;
    internal const string ConflictMessage = "oh no reminder changed before saving";

    private readonly IClock _clock;
    private readonly IReminderRepository _reminders;
    private readonly Func<string, CancellationToken, Task> _dismissNotificationAsync;
    private readonly Func<string, CancellationToken, Task> _discardHeldAsync;
    private readonly Func<PetEvent, CancellationToken, Task>? _presentPetAsync;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _awaitingAnswer = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Raised with the reminder id whenever a reminder row may have changed
    /// under an open Reminders page: after any Done/Snooze (toast or page) and
    /// after the engine announces an occurrence (which advanced the row).
    /// Raised on whichever thread did the work; handlers must marshal to the
    /// UI thread themselves and must not throw (a throwing handler is
    /// reported and ignored).
    /// </summary>
    public event Action<string>? ReminderChanged;

    /// <summary>Records that the engine announced an occurrence, so a later
    /// Done/Snooze from the page answers it instead of acting on the next,
    /// not-yet-due occurrence. Never throws.</summary>
    public void RecordAnnounced(ReminderOccurrence occurrence)
    {
        if (occurrence is null || string.IsNullOrWhiteSpace(occurrence.ReminderId))
        {
            return;
        }

        _awaitingAnswer[occurrence.ReminderId] = occurrence.DueUtc;
        RaiseChanged(occurrence.ReminderId, Operation);
    }

    /// <summary>True while an announced occurrence of this reminder has not
    /// been answered with Done or Snooze yet (in this app session).</summary>
    public bool IsAwaitingAnswer(string reminderId) =>
        !string.IsNullOrEmpty(reminderId) && _awaitingAnswer.ContainsKey(reminderId);

    /// <summary>Toast Done: acknowledges the occurrence the toast announced.
    /// Always succeeds; each cleanup step is best-effort.</summary>
    public async Task<bool> CompleteAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        await AcknowledgeAsync(reminderId, Operation, cancellationToken);
        return true;
    }

    /// <summary>Toast Snooze: brings the announced occurrence back in 15
    /// minutes. Returns false (after reporting) when the reschedule could not
    /// be saved; the toast is then left in Action Center.</summary>
    public async Task<bool> SnoozeAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        try
        {
            await RescheduleAsync(reminderId, announced: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(Operation, exception);
            return false;
        }

        await AcknowledgeAsync(reminderId, Operation, cancellationToken);
        return true;
    }

    /// <summary>
    /// Page Done. Acknowledges an announced occurrence (same as the toast);
    /// otherwise completes the reminder ahead of time by consuming its pending
    /// occurrence. A one-time reminder that already fired (no pending
    /// occurrence) is only acknowledged. Throws
    /// <see cref="InvalidOperationException"/> when the row kept changing.
    /// </summary>
    public async Task<ReminderActionResult> CompleteFromPageAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var reminder = await FindAsync(reminderId, cancellationToken);
            if (reminder is null)
            {
                await AcknowledgeAsync(reminderId, PageOperation, cancellationToken);
                return new ReminderActionResult(ReminderAnswer.Missing);
            }

            if (IsAwaitingAnswer(reminderId) || reminder.NextDueUtc is null)
            {
                await AcknowledgeAsync(reminderId, PageOperation, cancellationToken);
                return new ReminderActionResult(ReminderAnswer.Acknowledged, reminder);
            }

            var now = _clock.UtcNow.ToUniversalTime();
            var next = ReminderScheduler.NextOccurrenceAfterCompletion(
                reminder,
                now,
                ResolveTimeZone(reminder.LocalTimeZoneId));
            if (await _reminders.RecordOccurrencesAndAdvanceAsync(
                    reminder,
                    [new ReminderOccurrence(reminder.Id, now)],
                    next,
                    cancellationToken))
            {
                // The completion is durable; the rest is best-effort cleanup
                // of any toast/pet/held copy that is still around.
                await AcknowledgeAsync(reminderId, PageOperation, cancellationToken);
                return new ReminderActionResult(
                    ReminderAnswer.CompletedAhead,
                    reminder with { NextDueUtc = next, SnoozedUntilUtc = null });
            }

            // Lost the compare-and-set (an edit or engine tick won): re-read
            // and decide again -- the engine may just have announced it.
        }

        throw new InvalidOperationException(ConflictMessage);
    }

    /// <summary>
    /// Page Snooze, with the same rescheduling rules as the toast for an
    /// announced occurrence. Nothing is cleared when there was nothing to
    /// snooze (<see cref="ReminderAnswer.NotDueYet"/>), so a held copy is
    /// never discarded without the reminder being brought back. Throws
    /// <see cref="InvalidOperationException"/> when the row kept changing.
    /// </summary>
    public async Task<ReminderActionResult> SnoozeFromPageAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        var result = await RescheduleAsync(reminderId, IsAwaitingAnswer(reminderId), cancellationToken);
        if (result.Answer != ReminderAnswer.NotDueYet)
        {
            await AcknowledgeAsync(reminderId, PageOperation, cancellationToken);
        }

        return result;
    }

    private async Task<ReminderActionResult> RescheduleAsync(
        string reminderId,
        bool announced,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var reminder = await FindAsync(reminderId, cancellationToken);
            if (reminder is null)
            {
                return new ReminderActionResult(ReminderAnswer.Missing);
            }

            if (!reminder.Enabled)
            {
                // Switched off since it was announced: there is nothing to
                // bring back, and a snooze must not re-enable it.
                return new ReminderActionResult(ReminderAnswer.SwitchedOff, reminder);
            }

            var now = _clock.UtcNow.ToUniversalTime();
            var until = now + SnoozeDuration;
            var next = reminder.NextDueUtc?.ToUniversalTime();
            // A one-time reminder with no pending occurrence has already fired
            // (or was completed): snoozing brings it back once more.
            if (announced || next is null)
            {
                if (next is { } upcoming && upcoming > now && upcoming <= until)
                {
                    return new ReminderActionResult(ReminderAnswer.NextComesSooner, reminder);
                }
            }
            else if (next > until)
            {
                // Not announced and not due within the window: the old
                // SnoozedUntilUtc write here was silently ignored by the
                // scheduler; say so instead of pretending.
                return new ReminderActionResult(ReminderAnswer.NotDueYet, reminder);
            }

            if (await _reminders.RecordOccurrencesAndAdvanceAsync(reminder, [], until, cancellationToken))
            {
                return new ReminderActionResult(
                    ReminderAnswer.Snoozed,
                    reminder with { NextDueUtc = until, SnoozedUntilUtc = null });
            }

            // Compare-and-set lost to a concurrent edit or engine tick; re-read
            // and decide again against the row that won.
        }

        throw new InvalidOperationException(ConflictMessage);
    }

    private async Task<Reminder?> FindAsync(string reminderId, CancellationToken cancellationToken) =>
        (await _reminders.ListAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, reminderId, StringComparison.Ordinal));

    private async Task AcknowledgeAsync(string reminderId, string operation, CancellationToken cancellationToken)
    {
        _awaitingAnswer.TryRemove(reminderId, out _);
        if (_presentPetAsync is not null)
        {
            await BestEffortAsync(
                () => _presentPetAsync(new PetEvent.Dismissed(reminderId), cancellationToken),
                operation,
                cancellationToken);
        }

        await BestEffortAsync(() => _dismissNotificationAsync(reminderId, cancellationToken), operation, cancellationToken);
        // A copy held back by quiet hours/fullscreen/pause would otherwise
        // surface the same occurrence again after she already answered it.
        await BestEffortAsync(() => _discardHeldAsync(reminderId, cancellationToken), operation, cancellationToken);
        RaiseChanged(reminderId, operation);
    }

    private void RaiseChanged(string reminderId, string operation)
    {
        if (ReminderChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(reminderId);
            }
            catch (Exception exception)
            {
                Report(operation, exception);
            }
        }
    }

    private async Task BestEffortAsync(Func<Task> step, string operation, CancellationToken cancellationToken)
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
            Report(operation, exception);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
            or InvalidTimeZoneException
            or ArgumentException)
        {
            // Same fallback as ReminderEngine: an unknown zone must not make
            // "done" fail for good.
            return TimeZoneInfo.Utc;
        }
    }

    private void Report(string operation, Exception exception)
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
