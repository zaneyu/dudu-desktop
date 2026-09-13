using System.Diagnostics;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.App.Presentation;

/// <summary>
/// Converts a committed reminder occurrence into the corresponding
/// <see cref="Dudu.Core.Pet.PetEvent.ReminderDue"/> and notification
/// request, routed through the one presentation gateway so quiet hours,
/// focus, fullscreen, session lock, and pause are enforced uniformly. This
/// keeps Dudu.Core independent of WinUI and Windows notifications: the
/// gateway is resolved lazily because it is not composed yet at the point
/// this sink is registered with dependency injection.
/// </summary>
public sealed class ReminderDueSink : IReminderDueSink
{
    private readonly IReminderRepository _reminders;
    private readonly Func<PresentationCoordinator> _gateway;

    public ReminderDueSink(
        IReminderRepository reminders,
        Func<PresentationCoordinator> gateway)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task NotifyAsync(
        ReminderOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        try
        {
            var reminders = await _reminders.ListAsync(cancellationToken);
            var reminder = reminders.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, occurrence.ReminderId, StringComparison.Ordinal));
            if (reminder is null)
            {
                return;
            }

            var item = DurableNotification.Reminder(reminder.Id, reminder.Title);
            var bypass = reminder.QuietHoursBehavior == QuietHoursBehavior.DeliverImmediately;
            await _gateway().PublishAsync(item, bypass, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The reminder occurrence is already durably recorded by
            // ReminderEngine before this sink runs; a presentation failure
            // here must never roll that back or break the reminder tick.
            Trace.TraceError("Dudu reminder presentation failed: {0}", exception);
        }
    }
}
