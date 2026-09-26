using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Policies;
using Dudu.Core.Reminders;

namespace Dudu.App.ViewModels;

public enum ReminderScheduleKind
{
    Once,
    Daily,
    SelectedWeekdays,
    Interval,
}

public sealed class RemindersViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private Reminder? _selectedReminder;
    private Reminder? _pendingDeleteReminder;
    private string _title = string.Empty;
    private string? _details;
    private ReminderScheduleKind _scheduleKind = ReminderScheduleKind.Once;
    private TimeOnly _localTime = new(9, 0);
    private int _intervalMinutes = 60;
    private bool _enabled = true;
    private QuietHoursBehavior _quietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd;
    private bool _hydrationEnabled;
    private bool _breakEnabled;
    private bool _eveningCheckInEnabled;
    private bool _bedtimeRitualEnabled;
    private IReadOnlySet<DayOfWeek> _selectedWeekdays = DefaultWeekdays();

    public RemindersViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _hydrationEnabled = context.CurrentPreferences.HydrationRemindersEnabled;
        _breakEnabled = context.CurrentPreferences.BreakRemindersEnabled;
        _eveningCheckInEnabled = context.CurrentPreferences.EveningCheckInEnabled;
        _bedtimeRitualEnabled = context.CurrentPreferences.BedtimeRitualEnabled;
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        SaveCommand = new AsyncRelayCommand((CancellationToken ct) => SaveAsync(ct));
        NewReminderCommand = new RelayCommand(NewReminder);
        CompleteCommand = new AsyncRelayCommand<Reminder?>((item, ct) => CompleteAsync(item, ct));
        SnoozeCommand = new AsyncRelayCommand<Reminder?>((item, ct) => SnoozeAsync(item, ct));
        SaveReminderPreferencesCommand = new AsyncRelayCommand(
            (CancellationToken ct) => SaveReminderPreferencesAsync(ct));
        RequestDeleteReminderCommand = new RelayCommand<Reminder?>(RequestDeleteReminder);
        DeleteReminderCommand = new AsyncRelayCommand<Reminder?>((item, ct) => DeleteReminderAsync(item, ct));
        CancelDeleteReminderCommand = new RelayCommand(() => PendingDeleteReminder = null);
        Reminders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoReminders));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand NewReminderCommand { get; }
    public IAsyncRelayCommand<Reminder?> CompleteCommand { get; }
    public IAsyncRelayCommand<Reminder?> SnoozeCommand { get; }
    public IAsyncRelayCommand SaveReminderPreferencesCommand { get; }
    public IRelayCommand<Reminder?> RequestDeleteReminderCommand { get; }
    public IAsyncRelayCommand<Reminder?> DeleteReminderCommand { get; }
    public IRelayCommand CancelDeleteReminderCommand { get; }

    public ObservableCollection<Reminder> Reminders { get; } = [];

    /// <summary>Drives the list's empty-state copy.</summary>
    public bool HasNoReminders => Reminders.Count == 0;

    /// <summary>The reminder the editor is bound to. The editor fields are loaded
    /// (or reset, for null) BEFORE the change notification is raised: the page
    /// mirrors the unbound schedule/time/weekday controls from this notification,
    /// and used to read the previously selected reminder's schedule because the
    /// fields were only copied after it fired -- so a later save silently
    /// rewrote the reminder with the wrong schedule. A null selection resets the
    /// editor so a deselected reminder's text cannot be saved as a duplicate.</summary>
    public Reminder? SelectedReminder
    {
        get => _selectedReminder;
        set
        {
            if (EqualityComparer<Reminder?>.Default.Equals(_selectedReminder, value)) return;
            if (value is null) ResetEditor();
            else LoadEditor(value);
            _selectedReminder = value;
            OnPropertyChanged();
            // The pending confirmation names a specific reminder; once the user looks
            // at something else, confirming must not delete the one left behind.
            if (PendingDeleteReminder is not null && PendingDeleteReminder.Id != value?.Id)
            {
                PendingDeleteReminder = null;
            }
        }
    }

    public Reminder? PendingDeleteReminder
    {
        get => _pendingDeleteReminder;
        private set
        {
            if (SetProperty(ref _pendingDeleteReminder, value))
            {
                OnPropertyChanged(nameof(IsConfirmingDeleteReminder));
                OnPropertyChanged(nameof(DeleteReminderPrompt));
            }
        }
    }

    public bool IsConfirmingDeleteReminder => PendingDeleteReminder is not null;

    public string? DeleteReminderPrompt => PendingDeleteReminder is null
        ? null
        : $"delete \"{PendingDeleteReminder.Title}\" for good? cannot undo";

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? Details { get => _details; set => SetProperty(ref _details, value); }
    public ReminderScheduleKind ScheduleKind
    {
        get => _scheduleKind;
        set
        {
            if (!SetProperty(ref _scheduleKind, value)) return;
            OnPropertyChanged(nameof(ScheduleIndex));
            OnPropertyChanged(nameof(UsesLocalTime));
            OnPropertyChanged(nameof(UsesWeekdays));
            OnPropertyChanged(nameof(UsesInterval));
        }
    }

    /// <summary>Only the fields the chosen schedule actually reads are editable, so
    /// the weekday boxes or interval never look like they apply when they don't.</summary>
    public bool UsesLocalTime => ScheduleKind != ReminderScheduleKind.Interval;
    public bool UsesWeekdays => ScheduleKind == ReminderScheduleKind.SelectedWeekdays;
    public bool UsesInterval => ScheduleKind == ReminderScheduleKind.Interval;
    public int ScheduleIndex
    {
        get => (int)ScheduleKind;
        set
        {
            if (Enum.IsDefined((ReminderScheduleKind)value)) ScheduleKind = (ReminderScheduleKind)value;
        }
    }
    public TimeOnly LocalTime
    {
        get => _localTime;
        set
        {
            if (SetProperty(ref _localTime, value)) OnPropertyChanged(nameof(LocalTimeText));
        }
    }
    public string LocalTimeText
    {
        get => LocalTime.ToString("HH:mm");
        set
        {
            if (TimeOnly.TryParse(value, out var parsed)) LocalTime = parsed;
        }
    }
    public int IntervalMinutes { get => _intervalMinutes; set => SetProperty(ref _intervalMinutes, Math.Clamp(value, 1, 10080)); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public QuietHoursBehavior QuietHoursBehavior { get => _quietHoursBehavior; set => SetProperty(ref _quietHoursBehavior, value); }
    public bool HydrationRemindersEnabled { get => _hydrationEnabled; set => SetProperty(ref _hydrationEnabled, value); }
    public bool BreakRemindersEnabled { get => _breakEnabled; set => SetProperty(ref _breakEnabled, value); }
    public bool EveningCheckInEnabled { get => _eveningCheckInEnabled; set => SetProperty(ref _eveningCheckInEnabled, value); }
    public bool BedtimeRitualEnabled { get => _bedtimeRitualEnabled; set => SetProperty(ref _bedtimeRitualEnabled, value); }
    public IReadOnlySet<DayOfWeek> SelectedWeekdays
    {
        get => _selectedWeekdays;
        set => SetProperty(ref _selectedWeekdays, value ?? DefaultWeekdays());
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            var preferences = _context.CurrentPreferences;
            var items = await _context.Reminders.ListAsync(ct);
            await MutateAsync(() =>
            {
                HydrationRemindersEnabled = preferences.HydrationRemindersEnabled;
                BreakRemindersEnabled = preferences.BreakRemindersEnabled;
                EveningCheckInEnabled = preferences.EveningCheckInEnabled;
                BedtimeRitualEnabled = preferences.BedtimeRitualEnabled;
                var selected = SelectedReminder;
                Reminders.Clear();
                foreach (var reminder in items)
                {
                    Reminders.Add(reminder);
                }

                // Never auto-select: an auto-selected first reminder put the
                // "create or edit" form into edit mode on every visit, so typing
                // a new reminder silently overwrote an existing one. Keep an
                // explicit selection pointing at the fresh row instead, so the
                // complete/snooze buttons don't act on a stale copy (which the
                // compare-and-set store rejects as "reminder changed").
                if (selected is not null)
                {
                    var fresh = Reminders.FirstOrDefault(item => item.Id == selected.Id);
                    if (fresh is null) SelectedReminder = null;
                    else RebindSelection(fresh);
                }

                // The shell caches pages/view models across visits: a stale pending
                // confirmation from a previous visit must not resurface on this one.
                PendingDeleteReminder = null;
            }, ct);
        }, cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = (Title ?? string.Empty).Trim();
            if (title.Length == 0) throw new ArgumentException("aiyo add a title first", nameof(Title));
            if (ScheduleKind == ReminderScheduleKind.SelectedWeekdays && SelectedWeekdays.Count == 0)
            {
                // Used to be silently replaced with Monday.
                throw new ArgumentException("pick at least one day first", nameof(SelectedWeekdays));
            }
            var rule = BuildRule();
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var zone = SelectedReminder is null
                ? _context.Clock.LocalTimeZone
                : ResolveTimeZone(SelectedReminder.LocalTimeZoneId);
            var nextDue = NextDueUtc(rule, now, zone);
            var isEveningRoutine = SelectedReminder?.Id is LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId;
            var reminder = SelectedReminder is null
                ? new Reminder(Guid.NewGuid().ToString("N"), title, Normalize(Details), Enabled, rule,
                    zone.Id, QuietHoursBehavior, MissedOccurrencePolicy.LatestOnly, nextDue,
                    QuietHours: _context.CurrentPreferences.QuietHours)
                : SelectedReminder with
                {
                    Title = title,
                    Details = Normalize(Details),
                    Enabled = Enabled,
                    Rule = rule,
                    QuietHoursBehavior = isEveningRoutine ? QuietHoursBehavior.WaitUntilQuietHoursEnd : QuietHoursBehavior,
                    MissedPolicy = isEveningRoutine ? MissedOccurrencePolicy.Skip : SelectedReminder.MissedPolicy,
                    NextDueUtc = nextDue,
                    QuietHours = isEveningRoutine ? null : _context.CurrentPreferences.QuietHours,
                };
            await _context.ReminderWriter.SaveAsync(reminder, cancellationToken);
            var saved = reminder;
            await MutateAsync(() => Replace(saved), cancellationToken);
            NewReminder();
        }, "oki reminder saved");

    /// <summary>Clears the editor for a fresh reminder. Runs after every save so typing
    /// a fresh title afterward creates a new reminder instead of silently overwriting
    /// the one just saved.</summary>
    public void NewReminder()
    {
        if (_selectedReminder is not null)
        {
            SelectedReminder = null;
            return;
        }

        // Nothing was selected (e.g. a brand-new reminder was just saved), so
        // the setter would not fire: still reset and notify, or the page's
        // unbound schedule/time/weekday controls keep showing the last
        // reminder's values while the view model has reset to defaults.
        ResetEditor();
        OnPropertyChanged(nameof(SelectedReminder));
    }

    private void ResetEditor()
    {
        Title = string.Empty;
        Details = null;
        ScheduleKind = ReminderScheduleKind.Once;
        LocalTime = new TimeOnly(9, 0);
        IntervalMinutes = 60;
        Enabled = true;
        QuietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd;
        SelectedWeekdays = DefaultWeekdays();
    }

    private void LoadEditor(Reminder value)
    {
        Title = value.Title;
        Details = value.Details;
        Enabled = value.Enabled;
        QuietHoursBehavior = value.QuietHoursBehavior;
        // Reset the schedule fields first so values from a previously selected
        // reminder (its weekdays, interval or time) never leak into this one.
        LocalTime = new TimeOnly(9, 0);
        IntervalMinutes = 60;
        SelectedWeekdays = DefaultWeekdays();
        switch (value.Rule)
        {
            case RecurrenceRule.Daily daily:
                ScheduleKind = ReminderScheduleKind.Daily;
                LocalTime = daily.LocalTime;
                break;
            case RecurrenceRule.SelectedWeekdays weekdays:
                ScheduleKind = ReminderScheduleKind.SelectedWeekdays;
                LocalTime = weekdays.LocalTime;
                if (weekdays.Days is { Count: > 0 } days) SelectedWeekdays = new HashSet<DayOfWeek>(days);
                break;
            case RecurrenceRule.Interval interval:
                ScheduleKind = ReminderScheduleKind.Interval;
                IntervalMinutes = Math.Max(1, (int)interval.Period.TotalMinutes);
                break;
            default:
                ScheduleKind = ReminderScheduleKind.Once;
                // A one-off reminder's time lives only in NextDueUtc: show it in
                // the reminder's own zone so re-saving keeps the same time.
                if (value.NextDueUtc is { } due)
                {
                    try
                    {
                        var local = TimeZoneInfo.ConvertTime(due, ResolveTimeZone(value.LocalTimeZoneId));
                        LocalTime = TimeOnly.FromTimeSpan(new TimeSpan(local.Hour, local.Minute, 0));
                    }
                    catch (Exception exception) when (exception is TimeZoneNotFoundException
                        or InvalidTimeZoneException or ArgumentException)
                    {
                        // Unknown zone: keep the neutral default time.
                    }
                }
                break;
        }
    }

    /// <summary>Points the selection at a fresh copy of the same reminder without
    /// reloading the editor, so unsaved edits survive a list refresh.</summary>
    private void RebindSelection(Reminder fresh)
    {
        if (ReferenceEquals(_selectedReminder, fresh)) return;
        _selectedReminder = fresh;
        OnPropertyChanged(nameof(SelectedReminder));
    }

    private static HashSet<DayOfWeek> DefaultWeekdays() =>
        [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];

    private void RequestDeleteReminder(Reminder? reminder)
    {
        if (reminder is null)
        {
            ErrorMessage = SelectOneFirstMessage;
            return;
        }

        if (IsBuiltInDefault(reminder.Id))
        {
            // Saving "helpful defaults" recreates these rows, so a delete would
            // quietly come back; point her at the switch that actually works.
            ErrorMessage = "this one comes from helpful defaults, turn it off there instead";
            return;
        }

        ErrorMessage = null;
        PendingDeleteReminder = reminder;
    }

    public Task DeleteReminderAsync(Reminder? reminder, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reminder);
            if (IsBuiltInDefault(reminder.Id))
            {
                throw new InvalidOperationException("this one comes from helpful defaults, turn it off there instead");
            }

            await _context.ReminderWriter.DeleteAsync(reminder.Id, cancellationToken);
            await MutateAsync(() =>
            {
                var existing = Reminders.FirstOrDefault(item => item.Id == reminder.Id);
                if (existing is not null) Reminders.Remove(existing);
                if (PendingDeleteReminder?.Id == reminder.Id) PendingDeleteReminder = null;
                if (SelectedReminder?.Id == reminder.Id) NewReminder();
            }, cancellationToken);

            // The row is already gone; the rest is best-effort cleanup so a toast
            // or a held copy of a deleted reminder never surfaces later.
            try
            {
                await _context.DismissReminderNotificationAsync(reminder.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-delete notification dismiss failed: {0}", exception.GetType().Name);
            }

            try
            {
                await _context.DiscardHeldReminderAsync(reminder.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-delete held-copy discard failed: {0}", exception.GetType().Name);
            }
        }, "otayyy reminder deleted le");

    private static bool IsBuiltInDefault(string id) =>
        id is "default-hydration" or "default-break"
            or LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId;

    public Task CompleteAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reminder);
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var zone = ResolveTimeZone(reminder.LocalTimeZoneId);
            var next = ReminderScheduler.NextOccurrenceAfterCompletion(reminder, now, zone);
            var occurrence = new ReminderOccurrence(reminder.Id, now);
            if (!await _context.Reminders.RecordOccurrencesAndAdvanceAsync(reminder, [occurrence], next, cancellationToken))
            {
                throw new InvalidOperationException("oh no reminder changed before saving");
            }
            // The reminder is already durably completed above -- everything
            // from here on is best-effort cleanup of other subsystems'
            // notion of this reminder, and must not turn a completion that
            // already succeeded into a reported failure.
            try
            {
                await _context.PresentPetAsync(new Dudu.Core.Pet.PetEvent.Dismissed(reminder.Id), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-complete pet present failed: {0}", exception);
            }

            var updated = reminder with { NextDueUtc = next, SnoozedUntilUtc = null };
            await MutateAsync(() => Replace(updated), cancellationToken);

            try
            {
                await _context.DismissReminderNotificationAsync(reminder.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-complete notification dismiss failed: {0}", exception);
            }

            try
            {
                // Completing here is a direct advance of the reminder row, not a
                // PresentationCoordinator.PresentAsync call -- so a copy that was
                // separately queued or held back (e.g. it became due while she
                // had Dudu hidden, or during quiet hours) would otherwise still
                // be sitting in that gateway's queue/persisted row and surface
                // again on a later tick or the next app launch, even though it
                // was just completed here.
                await _context.DiscardHeldReminderAsync(reminder.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-complete held-copy discard failed: {0}", exception);
            }
        }, "yayyy done le good job");
    }

    public Task SnoozeAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reminder);
            var snoozeUntil = _context.Clock.UtcNow.ToUniversalTime().AddMinutes(15);
            var snoozed = reminder with { SnoozedUntilUtc = snoozeUntil };
            await _context.ReminderWriter.SaveAsync(snoozed, cancellationToken);
            await MutateAsync(() => Replace(snoozed), cancellationToken);

            // The snooze is already durably saved above -- everything from
            // here on is best-effort cleanup of other subsystems' notion of
            // this reminder, and must not turn a snooze that already
            // succeeded into a reported failure (and a dismiss failure must
            // not skip the discard check below).
            try
            {
                await _context.DismissReminderNotificationAsync(reminder.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu reminder-snooze notification dismiss failed: {0}", exception);
            }

            // Unlike CompleteAsync, this can't unconditionally discard the held
            // copy. The scheduling engine advances NextDueUtc *before*
            // notifying, so once an occurrence is held, that held row is the
            // only record of it. If NextDueUtc is already later than this
            // snooze resolves, the snooze is "dead" -- SnoozedUntilUtc <
            // NextDueUtc is ignored by LoadDueAsync/Reconcile -- and discarding
            // the held copy here would make the reminder vanish instead of
            // resurfacing once the hold clears. Only discard when the snooze
            // is "live", i.e. it reaches at least as far as NextDueUtc and so
            // will actually govern re-delivery on its own.
            if (reminder.NextDueUtc is { } due && due.ToUniversalTime() <= snoozeUntil)
            {
                try
                {
                    await _context.DiscardHeldReminderAsync(reminder.Id, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    global::System.Diagnostics.Trace.TraceError("Dudu reminder-snooze held-copy discard failed: {0}", exception);
                }
            }
        }, "otayyy snoozed for 15 min");
    }

    public Task SaveReminderPreferencesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.UpdatePreferencesAndDefaultRemindersAsync(current => current with
            {
                HydrationRemindersEnabled = HydrationRemindersEnabled,
                BreakRemindersEnabled = BreakRemindersEnabled,
                EveningCheckInEnabled = EveningCheckInEnabled,
                BedtimeRitualEnabled = BedtimeRitualEnabled,
            }, cancellationToken);

            // These stable IDs make toggle changes an upsert, not a duplicate
            // or a stale disabled default left behind by initial hydration.
            // The transactional save itself (CompanionFeatureTransactionService)
            // preserves NextDueUtc/SnoozedUntilUtc for any default whose own
            // schedule this save did not touch.
            var defaults = (await _context.Reminders.ListAsync(cancellationToken))
                .Where(item => item.Id is "default-hydration" or "default-break"
                    or LocalReminderDefaults.EveningCheckInId or LocalReminderDefaults.BedtimeId)
                .ToArray();

            await MutateAsync(() =>
            {
                foreach (var reminder in defaults)
                {
                    Replace(reminder);
                }
            }, cancellationToken);
        }, "done le reminder prefs saved");

    private RecurrenceRule BuildRule() => ScheduleKind switch
    {
        ReminderScheduleKind.Daily => new RecurrenceRule.Daily(LocalTime),
        ReminderScheduleKind.SelectedWeekdays => new RecurrenceRule.SelectedWeekdays(
            new HashSet<DayOfWeek>(SelectedWeekdays), LocalTime),
        ReminderScheduleKind.Interval => new RecurrenceRule.Interval(TimeSpan.FromMinutes(IntervalMinutes)),
        _ => new RecurrenceRule.Once(),
    };

    private DateTimeOffset NextDueUtc(RecurrenceRule rule, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (rule is RecurrenceRule.Interval interval) return now.Add(interval.Period);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var localDate = localNow.Date.Date + LocalTime.ToTimeSpan();
        if (localDate <= localNow.DateTime) localDate = localDate.AddDays(1);
        if (rule is RecurrenceRule.SelectedWeekdays weekdays)
        {
            while (!weekdays.Days.Contains(localDate.DayOfWeek)) localDate = localDate.AddDays(1);
        }
        return ResolveLocal(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Wall-clock time to UTC that survives DST: a time skipped by a
    /// spring-forward gap moves to the first valid minute after it (ConvertTimeToUtc
    /// threw, surfacing raw framework text), and a repeated fall-back time uses its
    /// first occurrence -- the same rules ReminderScheduler uses.</summary>
    private static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 60)
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            return zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime())
                .Min();
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private void Replace(Reminder reminder)
    {
        var index = -1;
        for (var i = 0; i < Reminders.Count; i++)
        {
            if (string.Equals(Reminders[i].Id, reminder.Id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        var wasSelected = _selectedReminder?.Id == reminder.Id;
        if (index >= 0) Reminders[index] = reminder;
        else Reminders.Add(reminder);
        // Keep the selection (and the complete/snooze CommandParameter) on the
        // fresh row: a stale copy made a second "complete" fail the store's
        // compare-and-set with "reminder changed before saving".
        if (wasSelected) RebindSelection(reminder);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrEmpty(timeZoneId);
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        { return TimeZoneInfo.Utc; }
    }
}

/// <summary>Turns a reminder's recurrence rule into a short, lowercase, local-time
/// summary for the reminders list (e.g. "every day at 9:30 pm"). LocalTime and
/// Period are already the reminder's own local wall-clock values, so no time
/// zone conversion is needed here.</summary>
public static class ReminderScheduleSummary
{
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>List-row summary that also says when a reminder is switched off, so
    /// a disabled reminder no longer looks identical to an active one.</summary>
    public static string DescribeWithState(RecurrenceRule rule, bool enabled) =>
        enabled ? Describe(rule) : $"off · {Describe(rule)}";

    public static string Describe(RecurrenceRule rule) => rule switch
    {
        RecurrenceRule.Daily daily => $"every day at {FormatTime(daily.LocalTime)}",
        RecurrenceRule.SelectedWeekdays weekdays => DescribeWeekdays(weekdays),
        RecurrenceRule.Interval interval => $"every {FormatPeriod(interval.Period)}",
        _ => "once",
    };

    // Runs inside an x:Bind function binding during ListView item
    // realization, where a throw crashes the page -- a null or empty
    // weekday set (e.g. corrupt/old data) must fall back to plain copy
    // instead.
    private static string DescribeWeekdays(RecurrenceRule.SelectedWeekdays weekdays)
    {
        var days = FormatDays(weekdays.Days);
        return days.Length == 0
            ? $"every week at {FormatTime(weekdays.LocalTime)}"
            : $"on {days} at {FormatTime(weekdays.LocalTime)}";
    }

    private static string FormatTime(TimeOnly localTime) =>
        localTime.ToString("h:mm tt", global::System.Globalization.CultureInfo.InvariantCulture)
            .ToLowerInvariant();

    private static string FormatDays(IReadOnlySet<DayOfWeek>? days) => days is null || days.Count == 0
        ? string.Empty
        : string.Join(", ", WeekOrder.Where(days.Contains).Select(day => day.ToString()[..3].ToLowerInvariant()));

    private static string FormatPeriod(TimeSpan period)
    {
        if (period.TotalMinutes < 60 || period.TotalMinutes % 60 != 0)
        {
            var minutes = Math.Max(1, (int)period.TotalMinutes);
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        if (period.TotalMinutes % 1440 == 0)
        {
            var days = (int)period.TotalDays;
            return days switch
            {
                1 => "day",
                7 => "week",
                _ => $"{days} days",
            };
        }

        var hours = (int)period.TotalHours;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
