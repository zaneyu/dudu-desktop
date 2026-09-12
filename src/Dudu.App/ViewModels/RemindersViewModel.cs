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

    public RemindersViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _hydrationEnabled = context.CurrentPreferences.HydrationRemindersEnabled;
        _breakEnabled = context.CurrentPreferences.BreakRemindersEnabled;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(CancellationToken.None));
        CompleteCommand = new AsyncRelayCommand<Reminder>(CompleteAsync);
        SnoozeCommand = new AsyncRelayCommand<Reminder>(SnoozeAsync);
        SaveReminderPreferencesCommand = new AsyncRelayCommand(
            () => SaveReminderPreferencesAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand<Reminder> CompleteCommand { get; }
    public IAsyncRelayCommand<Reminder> SnoozeCommand { get; }
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
    public IReadOnlySet<DayOfWeek> SelectedWeekdays { get; set; } = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            Reminders.Clear();
            foreach (var reminder in await _context.Reminders.ListAsync(cancellationToken))
            {
                Reminders.Add(reminder);
            }

            SelectedReminder ??= Reminders.FirstOrDefault();
        });
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = Title.Trim();
            if (title.Length == 0) throw new ArgumentException("Add a reminder title.", nameof(Title));
            var rule = BuildRule();
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var nextDue = NextDueUtc(rule, now);
            var reminder = SelectedReminder is null
                ? new Reminder(Guid.NewGuid().ToString("N"), title, Normalize(Details), Enabled, rule,
                    TimeZoneInfo.Local.Id, QuietHoursBehavior, MissedOccurrencePolicy.LatestOnly, nextDue,
                    QuietHours: _context.CurrentPreferences.QuietHours)
                : SelectedReminder with
                {
                    Title = title,
                    Details = Normalize(Details),
                    Enabled = Enabled,
                    Rule = rule,
                    QuietHoursBehavior = QuietHoursBehavior,
                    NextDueUtc = nextDue,
                    QuietHours = _context.CurrentPreferences.QuietHours,
                };
            await _context.ReminderWriter.SaveAsync(reminder, cancellationToken);
            Replace(reminder);
            SelectedReminder = reminder;
        }, "Reminder saved.");

    public Task CompleteAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return RunAsync(async () =>
        {
            var now = _context.Clock.UtcNow.ToUniversalTime();
            var zone = TimeZoneInfo.Local;
            var next = ReminderScheduler.NextOccurrence(reminder with { SnoozedUntilUtc = null }, now, zone);
            var occurrence = new ReminderOccurrence(reminder.Id, now);
            await _context.Reminders.RecordOccurrencesAndAdvanceAsync(reminder, [occurrence], next, cancellationToken);
            await _context.PresentPetAsync(new Dudu.Core.Pet.PetEvent.Dismissed(reminder.Id), cancellationToken);
            Replace(reminder with { NextDueUtc = next ?? DateTimeOffset.MaxValue, SnoozedUntilUtc = null });
        }, "Reminder completed.");
    }

    public Task SnoozeAsync(Reminder? reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return RunAsync(async () =>
        {
            var snoozed = reminder with { SnoozedUntilUtc = _context.Clock.UtcNow.ToUniversalTime().AddMinutes(15) };
            await _context.ReminderWriter.SaveAsync(snoozed, cancellationToken);
            Replace(snoozed);
        }, "Snoozed for 15 minutes.");
    }

    public Task SaveReminderPreferencesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.UpdatePreferencesAndDefaultRemindersAsync(current => current with
            {
                HydrationRemindersEnabled = HydrationRemindersEnabled,
                BreakRemindersEnabled = BreakRemindersEnabled,
            }, cancellationToken);

            // These stable IDs make toggle changes an upsert, not a duplicate
            // or a stale disabled default left behind by initial hydration.
            foreach (var reminder in (await _context.Reminders.ListAsync(cancellationToken))
                .Where(item => item.Id is "default-hydration" or "default-break"))
            {
                Replace(reminder);
            }
        }, "Reminder preferences saved.");

    private RecurrenceRule BuildRule() => ScheduleKind switch
    {
        ReminderScheduleKind.Daily => new RecurrenceRule.Daily(LocalTime),
        ReminderScheduleKind.SelectedWeekdays => new RecurrenceRule.SelectedWeekdays(
            SelectedWeekdays.Count == 0 ? [DayOfWeek.Monday] : SelectedWeekdays, LocalTime),
        ReminderScheduleKind.Interval => new RecurrenceRule.Interval(TimeSpan.FromMinutes(IntervalMinutes)),
        _ => new RecurrenceRule.Once(),
    };

    private DateTimeOffset NextDueUtc(RecurrenceRule rule, DateTimeOffset now)
    {
        if (rule is RecurrenceRule.Interval interval) return now.Add(interval.Period);
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var localDate = localNow.Date.Date + LocalTime.ToTimeSpan();
        if (localDate <= localNow.DateTime) localDate = localDate.AddDays(1);
        if (rule is RecurrenceRule.SelectedWeekdays weekdays)
        {
            while (!weekdays.Days.Contains(localDate.DayOfWeek)) localDate = localDate.AddDays(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), TimeZoneInfo.Local));
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
}
