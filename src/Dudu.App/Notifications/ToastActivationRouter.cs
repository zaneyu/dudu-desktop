using System.Diagnostics;
using Dudu.App.Hosting;

namespace Dudu.App.Notifications;

/// <summary>
/// Decides what a parsed toast activation does. Kept free of WinUI and the
/// Windows App SDK (the production composition supplies navigation and toast
/// removal as delegates) so the routing runs in the cross-platform test
/// suite too.
/// </summary>
public static class ToastActivationRouter
{
    /// <summary>
    /// A note toast opens the Notes page and a reminder toast's body opens
    /// the Reminders page. A reminder toast's Done/Snooze buttons are carried
    /// out directly by <paramref name="reminderActions"/> -- they used to only
    /// open the Reminders page and change nothing -- and only when that is
    /// not possible (reminder gone or changed, a failed write, or no actions
    /// composed) is the toast dismissed and the Reminders page opened so she
    /// can finish it there.
    /// </summary>
    public static async Task HandleAsync(
        NotificationActivation activation,
        Func<string, CancellationToken, Task> navigateAsync,
        Func<string, CancellationToken, Task> dismissReminderToastAsync,
        ReminderToastActions? reminderActions,
        IAppHostErrorReporter? errorReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(navigateAsync);
        ArgumentNullException.ThrowIfNull(dismissReminderToastAsync);
        switch (activation.Action)
        {
            case NotificationActivationAction.OpenNote:
                await navigateAsync("notes", cancellationToken);
                return;
            case NotificationActivationAction.OpenReminder:
                await navigateAsync("reminders", cancellationToken);
                return;
            case NotificationActivationAction.ReminderDone:
            case NotificationActivationAction.ReminderSnooze:
                break;
            default:
                return;
        }

        if (activation.ReminderId is not { } reminderId)
        {
            return;
        }

        var applied = false;
        if (reminderActions is not null)
        {
            try
            {
                applied = activation.Action == NotificationActivationAction.ReminderDone
                    ? await reminderActions.CompleteAsync(reminderId, cancellationToken)
                    : await reminderActions.SnoozeAsync(reminderId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportFailure(errorReporter, "notification-reminder-action", exception);
            }
        }

        if (applied)
        {
            return;
        }

        // Acting on the toast still acknowledges it: drop the Action Center
        // copy before sending her to the page.
        try
        {
            await dismissReminderToastAsync(reminderId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(errorReporter, "notification-dismiss", exception);
        }

        await navigateAsync("reminders", cancellationToken);
    }

    private static void ReportFailure(IAppHostErrorReporter? errorReporter, string operation, Exception exception)
    {
        if (errorReporter is not null)
        {
            try { errorReporter.Report(operation, exception); }
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
