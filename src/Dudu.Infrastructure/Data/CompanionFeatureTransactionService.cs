using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;

namespace Dudu.Infrastructure.Data;

/// <summary>SQLite-backed cross-repository operations used by Task 14.</summary>
public sealed class CompanionFeatureTransactionService : ICompanionFeatureTransactions
{
    private readonly IAppUnitOfWork _unitOfWork;
    private readonly Func<string, CancellationToken, Task>? _faultInjector;

    public CompanionFeatureTransactionService(IAppUnitOfWork unitOfWork)
        : this(unitOfWork, null)
    {
    }

    internal CompanionFeatureTransactionService(
        IAppUnitOfWork unitOfWork,
        Func<string, CancellationToken, Task>? faultInjector)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _faultInjector = faultInjector;
    }

    public Task SavePreferencesAndDefaultRemindersAsync(
        Preferences preferences,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        return _unitOfWork.ExecuteAsync(async (context, token) =>
        {
            await context.Preferences.SaveAsync(preferences, token);
            await InjectFaultAsync("after-preferences", token);
            var writer = context.Reminders as IReminderWriter
                ?? throw new InvalidOperationException("The transactional reminder repository cannot save reminders.");
            foreach (var reminder in LocalReminderDefaults.Create(
                preferences,
                nowUtc.ToUniversalTime(),
                localTimeZone))
            {
                await writer.SaveAsync(reminder, token);
            }
            await InjectFaultAsync("after-default-reminders", token);
        }, cancellationToken);
    }

    public Task SaveRemoteNoteAndConsumeEnvelopeAsync(
        LocalLoveNote note,
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _unitOfWork.ExecuteAsync(async (context, token) =>
        {
            await context.LocalNotes.SaveToJarAsync(note, token);
            await InjectFaultAsync("after-note-save", token);
            if (!await context.RemoteEnvelopes.TryConsumeAsync(
                messageId,
                processedUtc.ToUniversalTime(),
                token))
            {
                throw new InvalidOperationException(
                    "That remote note was already consumed or is no longer available.");
            }
            await InjectFaultAsync("after-envelope-consume", token);
        }, cancellationToken);
    }

    public Task RestorePreferencesAndDefaultRemindersAsync(
        Preferences preferences,
        IReadOnlyList<Reminder> previousDefaultReminders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(previousDefaultReminders);
        return _unitOfWork.ExecuteAsync(async (context, token) =>
        {
            await context.Preferences.SaveAsync(preferences, token);
            var writer = context.Reminders as IReminderWriter
                ?? throw new InvalidOperationException("The transactional reminder repository cannot restore reminders.");
            await writer.DeleteAsync("default-hydration", token);
            await writer.DeleteAsync("default-break", token);
            await writer.DeleteAsync(LocalReminderDefaults.EveningCheckInId, token);
            await writer.DeleteAsync(LocalReminderDefaults.BedtimeId, token);
            foreach (var reminder in previousDefaultReminders)
            {
                if (reminder.Id is not ("default-hydration" or "default-break"
                    or LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId))
                {
                    throw new ArgumentException(
                        "Only stable default reminder rows can be restored.",
                        nameof(previousDefaultReminders));
                }
                await writer.SaveAsync(reminder, token);
            }
        }, cancellationToken);
    }

    private Task InjectFaultAsync(string point, CancellationToken cancellationToken) =>
        _faultInjector?.Invoke(point, cancellationToken) ?? Task.CompletedTask;
}
