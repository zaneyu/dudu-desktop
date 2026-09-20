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

            // LocalReminderDefaults.Create rebuilds every default from scratch,
            // which would otherwise drop an in-flight due time or snooze for a
            // default whose own schedule this save did not touch. Carry those
            // over from the existing row when nothing that determines the
            // schedule actually changed.
            var existing = (await context.Reminders.ListAsync(token))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);

            foreach (var reminder in LocalReminderDefaults.Create(
                preferences,
                nowUtc.ToUniversalTime(),
                localTimeZone))
            {
                var toSave = reminder;
                if (existing.TryGetValue(reminder.Id, out var previous))
                {
                    // Rule/QuietHoursBehavior/MissedPolicy are a hardcoded per-id
                    // default in LocalReminderDefaults.Create, never derived from
                    // Preferences -- so the existing row always wins for them,
                    // whether it holds the shipped default or an edit made on the
                    // Reminders page. Only Enabled is preference-driven (the
                    // hydration/break/bedtime checkboxes own it).
                    toSave = toSave with
                    {
                        Rule = previous.Rule,
                        QuietHoursBehavior = previous.QuietHoursBehavior,
                        MissedPolicy = previous.MissedPolicy,
                    };

                    if (previous.Enabled == reminder.Enabled
                        && previous.LocalTimeZoneId == reminder.LocalTimeZoneId)
                    {
                        // Nothing that determines the schedule changed: keep the
                        // existing due time and any in-flight snooze exactly as
                        // they were.
                        toSave = toSave with
                        {
                            NextDueUtc = previous.NextDueUtc,
                            SnoozedUntilUtc = previous.SnoozedUntilUtc,
                        };
                    }
                    else if (reminder.Enabled)
                    {
                        // Enabled here either because it is being re-enabled
                        // after being disabled, or because it stayed enabled
                        // but LocalTimeZoneId changed -- either way
                        // `reminder.NextDueUtc` above was computed by Create()
                        // for its own SHIPPED Daily rule, not the preserved
                        // (possibly edited) Rule just restored a few lines up,
                        // evaluated in the row's actual (new) time zone. An
                        // edited Interval(15m) rule must not resume at
                        // tomorrow's shipped 10:00, and an edited
                        // SelectedWeekdays rule must not fire on a day she
                        // never selected in the new zone. Recompute from the
                        // preserved rule instead, anchored on the row's last
                        // known due time and evaluated in the new zone; a
                        // stale snooze from before must not carry forward
                        // either.
                        //
                        // No `?? reminder.NextDueUtc` fallback: a preserved
                        // rule whose NextOccurrence is null (e.g. a completed
                        // Once-edited default, or one overdue past its own
                        // occurrence) genuinely has no next occurrence and
                        // must stay null, not silently resume on Create's
                        // shipped schedule.
                        //
                        // The anchor passed to NextOccurrence matters: when
                        // it is still in the future, NextOccurrence returns
                        // that UTC instant unchanged (net of quiet hours)
                        // rather than re-deriving a wall-clock occurrence --
                        // so anchoring on the OLD due time across a time-zone
                        // change would keep firing at the old zone's
                        // wall-clock instant translated literally into the
                        // new zone. Anchor on now instead whenever the zone
                        // changed, so the recompute falls through to the
                        // rule-based arms, which evaluate the next wall-clock
                        // occurrence in the (new) time zone. Re-enabling with
                        // the zone unchanged still anchors on the old due
                        // time, exactly as before.
                        var anchor = previous.LocalTimeZoneId == reminder.LocalTimeZoneId
                            || previous.NextDueUtc is null
                            ? previous.NextDueUtc
                            : nowUtc.ToUniversalTime();
                        toSave = toSave with
                        {
                            NextDueUtc = ReminderScheduler.NextOccurrence(
                                toSave with { NextDueUtc = anchor, SnoozedUntilUtc = null },
                                nowUtc.ToUniversalTime(),
                                localTimeZone),
                            SnoozedUntilUtc = null,
                        };
                    }
                    else
                    {
                        // Disabling: nothing will fire while disabled, but the
                        // row's real NextDueUtc must not be silently replaced
                        // by Create's shipped value here -- that value would
                        // later become the anchor for the re-enable recompute
                        // above, poisoning it the same way this finding's
                        // fall-through bug did.
                        toSave = toSave with { NextDueUtc = previous.NextDueUtc };
                    }

                    // The Reminders page edits Title/Details on a default
                    // reminder two-way. Only regenerate them here when the
                    // existing row still holds a recognized neutral/legacy
                    // default text for this id -- otherwise this save (which
                    // runs on every preferences change, whether or not this
                    // default's own schedule changed) would silently discard
                    // her rename.
                    if (!LocalReminderDefaults.IsKnownDefaultTitle(reminder.Id, previous.Title))
                    {
                        toSave = toSave with { Title = previous.Title };
                    }
                    // A null previous Details (she cleared the field) is a user
                    // edit too, not just a non-null one -- IsKnownDefaultDetails
                    // never matches string.Empty, so this still regenerates
                    // Details when the existing row holds the shipped/legacy
                    // text but preserves an explicit clear.
                    if (!LocalReminderDefaults.IsKnownDefaultDetails(reminder.Id, previous.Details ?? string.Empty))
                    {
                        toSave = toSave with { Details = previous.Details };
                    }
                }

                await writer.SaveAsync(toSave, token);
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
