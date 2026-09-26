using System.Diagnostics;
using Dudu.App.Hosting;

namespace Dudu.App.Notifications;

/// <summary>
/// Acts on a toast click while Dudu is running:
/// <list type="bullet">
/// <item>a body click opens the page the toast is about (Love Notes for a
/// note, Reminders for a reminder);</item>
/// <item>Done / Snooze on a reminder toast complete or snooze the reminder in
/// the background via <see cref="ReminderToastActions"/>, without pulling the
/// settings window up. If that is not possible (not composed yet, or the
/// save failed) the toast is dismissed only for Done, and the Reminders page
/// is opened so she can finish it there instead of the click being lost.</item>
/// </list>
/// Malformed or unknown activations are ignored. Never throws.
/// </summary>
public sealed class NotificationInvocationRouter
{
    public const string NavigateOperation = "notification-invoked";

    private readonly Func<string, CancellationToken, Task> _navigateAsync;
    private readonly Func<ReminderToastActions?> _reminderActions;
    private readonly Func<string, CancellationToken, Task> _dismissReminderAsync;
    private readonly IAppHostErrorReporter? _errorReporter;

    public NotificationInvocationRouter(
        Func<string, CancellationToken, Task> navigateAsync,
        Func<ReminderToastActions?> reminderActions,
        Func<string, CancellationToken, Task> dismissReminderAsync,
        IAppHostErrorReporter? errorReporter = null)
    {
        _navigateAsync = navigateAsync ?? throw new ArgumentNullException(nameof(navigateAsync));
        _reminderActions = reminderActions ?? throw new ArgumentNullException(nameof(reminderActions));
        _dismissReminderAsync = dismissReminderAsync ?? throw new ArgumentNullException(nameof(dismissReminderAsync));
        _errorReporter = errorReporter;
    }

    public async Task HandleAsync(
        IEnumerable<KeyValuePair<string, string>>? arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await HandleCoreAsync(NotificationActivation.TryParse(arguments), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(NavigateOperation, exception);
        }
    }

    private async Task HandleCoreAsync(NotificationActivation? activation, CancellationToken cancellationToken)
    {
        if (activation is null)
        {
            return;
        }

        if (activation.ReminderId is { } reminderId
            && activation.Action is NotificationActivationAction.ReminderDone
                or NotificationActivationAction.ReminderSnooze)
        {
            var actions = _reminderActions();
            var handled = actions is not null && (activation.Action == NotificationActivationAction.ReminderDone
                ? await actions.CompleteAsync(reminderId, cancellationToken)
                : await actions.SnoozeAsync(reminderId, cancellationToken));
            if (handled)
            {
                return;
            }

            if (activation.Action == NotificationActivationAction.ReminderDone)
            {
                // Done is an acknowledgement either way; drop the stale copy.
                try
                {
                    await _dismissReminderAsync(reminderId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Report(ReminderToastActions.Operation, exception);
                }
            }
        }

        await _navigateAsync(activation.Destination, cancellationToken);
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
