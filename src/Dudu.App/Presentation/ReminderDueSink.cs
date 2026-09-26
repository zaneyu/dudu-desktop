using System.Diagnostics;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;

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
    private readonly Func<IUnsolicitedPresentationGateway> _gateway;
    private readonly IProfileRepository? _profiles;
    private IAppHostErrorReporter? _errorReporter;

    public ReminderDueSink(
        IReminderRepository reminders,
        Func<IUnsolicitedPresentationGateway> gateway,
        IAppHostErrorReporter? errorReporter = null,
        IProfileRepository? profiles = null)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _errorReporter = errorReporter;
        _profiles = profiles;
    }

    /// <summary>
    /// Settable so the production composition can attach the shared AppHost
    /// sink after construction: the sink is registered (and eagerly resolved
    /// by <c>ReminderEngine</c> during <c>AppHost</c> construction) before
    /// the host — and therefore its error reporter — exists.
    /// </summary>
    public IAppHostErrorReporter? ErrorReporter
    {
        get => _errorReporter;
        set => _errorReporter = value;
    }

    private static DateTimeOffset NextLocalMidnight(DateTimeOffset dueUtc, string timeZoneId)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
            or InvalidTimeZoneException
            or ArgumentException)
        {
            // Mirror ReminderEngine.TickAsync: an unknown zone id (e.g. an IANA id on
            // Windows) falls back to UTC. The occurrence is already committed as
            // delivered, so throwing here would silently lose the reminder.
            timeZone = TimeZoneInfo.Utc;
        }

        var midnight = TimeZoneInfo.ConvertTime(dueUtc, timeZone).Date.AddDays(1);
        while (timeZone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(midnight))
        {
            return timeZone.GetAmbiguousTimeOffsets(midnight)
                .Select(offset => new DateTimeOffset(midnight, offset).ToUniversalTime())
                .Min();
        }

        return new DateTimeOffset(midnight, timeZone.GetUtcOffset(midnight)).ToUniversalTime();
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
            if (reminder is null || !reminder.Enabled)
            {
                return;
            }

            // No placeholder is ever persisted: PersonalizeTitle recognises the default
            // reminders' known neutral/legacy text and substitutes the recipient's name
            // only for those; a user-edited or non-default title passes through as-is.
            // The occurrence is already durably committed by this point, so a transient
            // failure reading the profile must not drop the whole notification -- it is
            // reported separately and the notify continues with the neutral copy.
            string? recipientName = null;
            if (_profiles is not null)
            {
                try
                {
                    recipientName = (await _profiles.GetAsync(cancellationToken))?.RecipientName;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportFailure("reminder-notify-profile", exception);
                }
            }

            var title = LocalReminderDefaults.PersonalizeTitle(reminder.Id, reminder.Title, recipientName);
            var details = reminder.Details is null
                ? null
                : LocalReminderDefaults.PersonalizeTitle(reminder.Id, reminder.Details, recipientName);

            var routine = reminder.Id is LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId;
            // The "details" she typed into the reminder editor used to be
            // dropped for every ordinary reminder, so they never appeared
            // when it fired. They now ride along as the bubble body (the
            // Windows toast still carries only the title). A blank details
            // field on the evening check-in no longer leaves a stray leading
            // space before the prompt.
            var hasDetails = !string.IsNullOrWhiteSpace(details);
            var body = reminder.Id == LocalReminderDefaults.EveningCheckInId
                ? hasDetails ? $"{details!.TrimEnd()} open Home to check in." : "open Home to check in."
                : hasDetails ? details : null;
            var item = DurableNotification.Reminder(
                reminder.Id,
                title,
                body: body,
                animationKey: reminder.Id == LocalReminderDefaults.BedtimeId ? "sticker-025" : null,
                expiresUtc: routine ? NextLocalMidnight(occurrence.DueUtc, reminder.LocalTimeZoneId) : null);
            var bypass = !routine && reminder.QuietHoursBehavior == QuietHoursBehavior.DeliverImmediately;
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
            // here must never roll that back or break the reminder tick. It
            // now leaves an operation-named diagnostic carrying the exception
            // type only — reminder content is never logged.
            ReportFailure("reminder-notify", exception);
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
