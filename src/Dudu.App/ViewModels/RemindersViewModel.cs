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
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand NewReminderCommand { get; }
    public IAsyncRelayCommand<Reminder?> CompleteCommand { get; }
    public IAsyncRelayCommand<Reminder?> SnoozeCommand { get; }
    public IAsyncRelayCommand SaveReminderPreferencesCommand { get; }

    public ObservableCollection<Reminder> Reminders { get; } = [];

    public Reminder? SelectedReminder
    {
        get => _selectedReminder;
        set
        {
            if (!SetProperty(ref _selectedReminder, value) || value is null) return;
            Title = value.Title;
            Details = value.Details;
            Enabled = value.Enabled;
            QuietHoursBehavior = value.QuietHoursBehavior;
            switch (value.Rule)
            {
                case RecurrenceRule.Daily daily:
                    ScheduleKind = ReminderScheduleKind.Daily;
                    LocalTime = daily.LocalTime;
                    break;
                case RecurrenceRule.SelectedWeekdays weekdays:
                    ScheduleKind = ReminderScheduleKind.SelectedWeekdays;
                    LocalTime = weekdays.LocalTime;
                    SelectedWeekdays = weekdays.Days;
                    break;
                case RecurrenceRule.Interval interval:
                    ScheduleKind = ReminderScheduleKind.Interval;
                    IntervalMinutes = Math.Max(1, (int)interval.Period.TotalMinutes);
                    break;
                default:
                    ScheduleKind = ReminderScheduleKind.Once;
                    break;
            }
        }
    }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? Details { get => _details; set => SetProperty(ref _details, value); }
    public ReminderScheduleKind ScheduleKind
    {
        get => _scheduleKind;
        set
        {
            if (SetProperty(ref _scheduleKind, value)) OnPropertyChanged(nameof(ScheduleIndex));
        }
    }
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
    public IReadOnlySet<DayOfWeek> SelectedWeekdays { get; set; } = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };

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
                Reminders.Clear();
                foreach (var reminder in items)
                {
                    Reminders.Add(reminder);
                }

                SelectedReminder ??= Reminders.FirstOrDefault();
            }, ct);
        }, cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = Title.Trim();
            if (title.Length == 0) throw new ArgumentException("aiyo add a title first", nameof(Title));
            var rule = BuildRule();
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var zone = SelectedReminder is null
                ? TimeZoneInfo.Local
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
        SelectedReminder = null;
        Title = string.Empty;
        Details = null;
        ScheduleKind = ReminderScheduleKind.Once;
        LocalTime = new TimeOnly(9, 0);
        IntervalMinutes = 60;
        Enabled = true;
        QuietHoursBehavior = QuietHoursBehavior.WaitUntilQuietHoursEnd;
        SelectedWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
    }

    public Task CompleteAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reminder);
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var zone = ResolveTimeZone(reminder.LocalTimeZoneId);
            var next = ReminderScheduler.NextOccurrenceAfterCompletion(reminder with { SnoozedUntilUtc = null }, now, zone);
            var occurrence = new ReminderOccurrence(reminder.Id, now);
            if (!await _context.Reminders.RecordOccurrencesAndAdvanceAsync(reminder, [occurrence], next, cancellationToken))
            {
                throw new InvalidOperationException("oh no reminder changed before saving");
            }
            await _context.PresentPetAsync(new Dudu.Core.Pet.PetEvent.Dismissed(reminder.Id), cancellationToken);
            var updated = reminder with { NextDueUtc = next, SnoozedUntilUtc = null };
            await MutateAsync(() => Replace(updated), cancellationToken);
            await _context.DismissReminderNotificationAsync(reminder.Id, cancellationToken);

            // Completing here is a direct advance of the reminder row, not a
            // PresentationCoordinator.PresentAsync call -- so a copy that was
            // separately queued or held back (e.g. it became due while she
            // had Dudu hidden, or during quiet hours) would otherwise still
            // be sitting in that gateway's queue/persisted row and surface
            // again on a later tick or the next app launch, even though it
            // was just completed here.
            await _context.DiscardHeldReminderAsync(reminder.Id, cancellationToken);
        }, "yayyy done le good job");
    }

    public Task SnoozeAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reminder);
            var snoozed = reminder with { SnoozedUntilUtc = _context.Clock.UtcNow.ToUniversalTime().AddMinutes(15) };
            await _context.ReminderWriter.SaveAsync(snoozed, cancellationToken);
            await MutateAsync(() => Replace(snoozed), cancellationToken);
            await _context.DismissReminderNotificationAsync(reminder.Id, cancellationToken);

            // Same reasoning as CompleteAsync: a snooze that was separately
            // queued or held back (quiet hours, Dudu hidden) would otherwise
            // still be sitting in that gateway's queue/persisted row and pop
            // again on a later tick, even though she just pushed it back.
            await _context.DiscardHeldReminderAsync(reminder.Id, cancellationToken);
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
            SelectedWeekdays.Count == 0 ? [DayOfWeek.Monday] : SelectedWeekdays, LocalTime),
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
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), zone));
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
        if (index >= 0) Reminders[index] = reminder;
        else Reminders.Add(reminder);
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
