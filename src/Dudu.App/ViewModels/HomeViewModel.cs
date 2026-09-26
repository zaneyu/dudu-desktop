using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.App.System;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;

namespace Dudu.App.ViewModels;

public sealed class HomeViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private Reminder? _nextReminder;
    private FocusSnapshot? _activeFocus;
    private Countdown? _selectedCountdown;
    private Countdown? _pendingDeleteCountdown;
    private CheckInSummary? _checkInSummary;
    private string _countdownTitle = string.Empty;
    private DateTimeOffset? _countdownTargetUtc;
    private MoodChoice _selectedMood = MoodChoice.Okay;
    private string? _checkInNote;
    private string _recipientName = string.Empty;

    public HomeViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        PetCommand = new AsyncRelayCommand((CancellationToken ct) => PetAsync(ct));
        PauseForOneHourCommand = new AsyncRelayCommand((CancellationToken ct) =>
            SetPauseAsync(PausePolicy.ForOneHour(_context.Clock.UtcNow), ct));
        ResumeCommand = new AsyncRelayCommand((CancellationToken ct) =>
            SetPauseAsync(PauseState.None, ct));
        CreateCountdownCommand = new AsyncRelayCommand((CancellationToken ct) =>
            SaveCountdownAsync(ct));
        SelectCountdownCommand = new RelayCommand<Countdown>(SelectCountdown);
        DeleteCountdownCommand = new AsyncRelayCommand<Countdown>((item, ct) => DeleteCountdownAsync(item, ct));
        RequestDeleteCountdownCommand = new RelayCommand<Countdown>(RequestDeleteCountdown);
        CancelDeleteCountdownCommand = new RelayCommand(() => PendingDeleteCountdown = null);
        RecordCheckInCommand = new AsyncRelayCommand((CancellationToken ct) =>
            RecordCheckInAsync(ct));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PetCommand { get; }
    public IAsyncRelayCommand PauseForOneHourCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand CreateCountdownCommand { get; }
    public IRelayCommand<Countdown> SelectCountdownCommand { get; }
    public IAsyncRelayCommand<Countdown> DeleteCountdownCommand { get; }
    public IRelayCommand<Countdown> RequestDeleteCountdownCommand { get; }
    public IRelayCommand CancelDeleteCountdownCommand { get; }
    public IAsyncRelayCommand RecordCheckInCommand { get; }

    public ObservableCollection<Countdown> Countdowns { get; } = [];
    public ObservableCollection<MoodCheckIn> RecentCheckIns { get; } = [];

    public PetPresentation PetPresentation => _context.Pet.Current;

    public bool IsPaused => _context.GetPauseState().Mode != PauseMode.None;

    public string PauseDescription => _context.GetPauseState() switch
    {
        { Mode: PauseMode.OneHour } => "dudu rest one hour",
        { Mode: PauseMode.FiveMinutes } => "dudu rest five minutes",
        { Mode: PauseMode.UntilTomorrowAtSeven } => "dudu sleep sleep till tmrw",
        { Mode: PauseMode.UntilFullscreenEnds } => "dudu rest till fullscreen done",
        { Mode: PauseMode.Indefinite } => "dudu rest till u say",
        _ => "dudu is here",
    };

    public Reminder? NextReminder
    {
        get => _nextReminder;
        private set
        {
            if (SetProperty(ref _nextReminder, value)) OnPropertyChanged(nameof(NextReminderText));
        }
    }

    public FocusSnapshot? ActiveFocus
    {
        get => _activeFocus;
        private set
        {
            if (SetProperty(ref _activeFocus, value)) OnPropertyChanged(nameof(ActiveFocusText));
        }
    }

    public Countdown? SelectedCountdown
    {
        get => _selectedCountdown;
        set
        {
            if (!SetProperty(ref _selectedCountdown, value)) return;
            // The pending confirmation names a specific countdown; once the user looks at
            // something else, confirming should not delete the countdown they left behind.
            if (PendingDeleteCountdown is not null && PendingDeleteCountdown.Id != value?.Id)
            {
                PendingDeleteCountdown = null;
            }
        }
    }

    public Countdown? PendingDeleteCountdown
    {
        get => _pendingDeleteCountdown;
        private set
        {
            if (SetProperty(ref _pendingDeleteCountdown, value))
            {
                OnPropertyChanged(nameof(IsConfirmingDeleteCountdown));
                OnPropertyChanged(nameof(DeleteCountdownPrompt));
            }
        }
    }

    public bool IsConfirmingDeleteCountdown => PendingDeleteCountdown is not null;

    public string? DeleteCountdownPrompt => PendingDeleteCountdown is null
        ? null
        : $"delete \"{PendingDeleteCountdown.Title}\" for good? cannot undo";

    public CheckInSummary? CheckInSummary
    {
        get => _checkInSummary;
        private set
        {
            if (SetProperty(ref _checkInSummary, value))
            {
                OnPropertyChanged(nameof(CheckInSummaryText));
                OnPropertyChanged(nameof(YesterdayReflectionText));
            }
        }
    }

    public string NextReminderText => NextReminder is null
        ? "no reminders yet ah"
        : NextReminder.NextDueUtc is { } nextDue
            ? $"next reminder {LocalReminderDefaults.PersonalizeTitle(NextReminder.Id, NextReminder.Title, _recipientName)} at {nextDue.ToLocalTime():g}"
            : "no reminders yet ah";

    /// <summary>Days remaining for the countdown due soonest, from the countdowns
    /// the page already loaded in <see cref="RefreshAsync"/>. A countdown whose
    /// local calendar date has already gone by is excluded -- otherwise it would
    /// sort first forever and read as "is today" long after it happened -- but one
    /// whose calendar date is today still reads "is today" regardless of the exact
    /// time it is due. Same-day countdowns tie-break on TargetUtc so the result is
    /// stable instead of depending on Countdowns' incidental ordering. "no
    /// countdowns yet ah" means the list itself is empty; when it has entries but
    /// every one of them has already gone by, that reads "nothing coming up"
    /// instead -- otherwise it would misreport an empty list.</summary>
    public string NextCountdownText
    {
        get
        {
            var nowUtc = _context.Clock.UtcNow;
            var upcoming = Countdowns
                .Select(countdown => (countdown, days: CalendarDaysUntil(countdown, nowUtc)))
                .Where(entry => entry.days is >= 0)
                .Select(entry => (entry.countdown, days: entry.days!.Value))
                .OrderBy(entry => entry.days)
                .ThenBy(entry => entry.countdown.TargetUtc ?? DateTimeOffset.MaxValue)
                .FirstOrDefault();

            if (upcoming.countdown is null)
            {
                return Countdowns.Count > 0 ? "nothing coming up" : "no countdowns yet ah";
            }

            return upcoming.days switch
            {
                0 => $"{upcoming.countdown.Title} is today",
                1 => $"{upcoming.countdown.Title} in 1 day",
                _ => $"{upcoming.countdown.Title} in {upcoming.days} days",
            };
        }
    }

    /// <summary>The target's local calendar date minus today's, or null when the
    /// countdown has no target at all. Computed directly (not via
    /// <see cref="Dudu.Core.Countdowns.CountdownService"/>, whose per-countdown
    /// display intentionally clamps an already-past date to zero) so a truly past
    /// countdown can be told apart from one due later today.</summary>
    private static int? CalendarDaysUntil(Countdown countdown, DateTimeOffset nowUtc)
    {
        if (countdown.TargetDate is null && countdown.TargetUtc is null) return null;

        var localNow = TimeZoneInfo.ConvertTime(nowUtc.ToUniversalTime(), countdown.LocalTimeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var targetDate = countdown.TargetDate
            ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(countdown.TargetUtc!.Value, countdown.LocalTimeZone).DateTime);
        return targetDate.DayNumber - today.DayNumber;
    }

    public string ActiveFocusText => DescribeFocus(ActiveFocus);

    /// <summary>Plain-language focus line, worded like the Tasks and Focus page. The
    /// old copy lower-cased the raw enum ("focus is endedearly with 0 min left") and
    /// always spoke in minutes ("focus is running with 90 min left").</summary>
    public static string DescribeFocus(FocusSnapshot? focus) =>
        FocusDisplay.Describe(focus, focus?.Remaining ?? TimeSpan.Zero);

    public string PetStateText => IsPaused
        ? "dudu is paused"
        : $"dudu is {DescribePetState(PetPresentation.State)}";

    /// <summary>Plain-language pet state. The raw enum name lower-cased read as
    /// "dudu is remotenote" / "focustransition" / "welcomeback"; the settings shell's
    /// companion panel shares this wording so both surfaces agree.</summary>
    public static string DescribePetState(PetState state) => state switch
    {
        PetState.Comfort => "comforting u",
        PetState.RemoteNote => "holding a note for u",
        PetState.Reminder => "showing a reminder",
        PetState.FocusTransition => "wrapping up a focus session",
        PetState.WelcomeBack => "saying welcome back",
        PetState.Ambient => "having a little moment",
        PetState.Focus => "keeping u company while u focus",
        _ => "idle",
    };

    /// <summary>Check-in history row label, matching the lower-case choices in the
    /// mood picker instead of the enum's "Great"/"Okay".</summary>
    public static string FormatCheckInChoice(MoodChoice choice) =>
        choice.ToString().ToLowerInvariant();

    /// <summary>Check-in history row time in the user's local time. The row used to
    /// bind the raw UTC DateTimeOffset, which rendered with a "+00:00" offset and
    /// the wrong hour for anyone outside UTC.</summary>
    public static string FormatCheckInTime(DateTimeOffset createdUtc) =>
        FormatCheckInTimeIn(createdUtc, TimeZoneInfo.Local);

    /// <summary>Zone-explicit form of <see cref="FormatCheckInTime(DateTimeOffset)"/>.
    /// Deliberately not an overload: x:Bind function bindings resolve by name.</summary>
    public static string FormatCheckInTimeIn(DateTimeOffset createdUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(createdUtc, zone).DateTime.ToString("g");
    }

    /// <summary>True when the free-text countdown target box already shows
    /// <paramref name="targetUtc"/> (blank for none). The box is not x:Bound -- it is
    /// parsed on every keystroke -- so HomePage uses this to re-sync the box only when
    /// the view model moved the target itself (e.g. cleared it after a save), without
    /// rewriting text the user is in the middle of typing.</summary>
    public static bool CountdownTargetTextMatches(string? text, DateTimeOffset? targetUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return targetUtc is null;
        return targetUtc is { } target
            && DateTimeOffset.TryParse(text, out var parsed)
            && parsed.ToUniversalTime() == target.ToUniversalTime();
    }

    public string PetAnimationText => $"current animation {PetPresentation.AnimationKey}";

    public string CheckInSummaryText => CheckInSummary is null
        ? "no check-ins yet ah"
        : $"{CheckInSummary.Recent.Count} optional check-in{(CheckInSummary.Recent.Count == 1 ? string.Empty : "s")} in the last 7 days";

    /// <summary>", name" when a recipient name is on file, or empty so
    /// copy built from it still reads naturally.</summary>
    private string RecipientClause => string.IsNullOrWhiteSpace(_recipientName) ? string.Empty : $", {_recipientName}";

    public string CheckInSectionHeading => $"how was your day{RecipientClause}?";

    public string YesterdayReflectionText
    {
        get
        {
            var zone = _context.Clock.LocalTimeZone;
            var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_context.Clock.UtcNow, zone).DateTime)
                .AddDays(-1);
            var previous = CheckInSummary?.Recent
                .Where(item => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.CreatedUtc, zone).DateTime) == yesterday)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            if (previous is null) return $"a fresh check-in whenever u feel like it{RecipientClause}";
            var reflection = string.IsNullOrWhiteSpace(previous.Note) ? string.Empty : $"\nyour note: {previous.Note}";
            return $"yesterday you chose {previous.Choice.ToString().ToLowerInvariant()}.{reflection}\nhow does today feel{RecipientClause}?";
        }
    }

    public string CountdownTitle
    {
        get => _countdownTitle;
        set => SetProperty(ref _countdownTitle, value);
    }

    public DateTimeOffset? CountdownTargetUtc
    {
        get => _countdownTargetUtc;
        set
        {
            if (SetProperty(ref _countdownTargetUtc, value?.ToUniversalTime()))
            {
                OnPropertyChanged(nameof(CountdownTargetText));
            }
        }
    }

    public string CountdownTargetText
    {
        get => CountdownTargetUtc?.ToLocalTime().ToString("g") ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) CountdownTargetUtc = null;
            else if (DateTimeOffset.TryParse(value, out var parsed)) CountdownTargetUtc = parsed;
        }
    }

    public MoodChoice SelectedMood
    {
        get => _selectedMood;
        set => SetProperty(ref _selectedMood, value);
    }

    public string? CheckInNote
    {
        get => _checkInNote;
        set => SetProperty(ref _checkInNote, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            var reminders = await _context.Reminders.ListAsync(ct);
            var focus = await _context.FocusService.GetCurrentAsync(ct);
            var countdowns = await _context.Countdowns.ListAsync(ct);
            var summary = await _context.CheckInService.SummarizeAsync(7, ct);
            var profile = await _context.Profiles.GetAsync(ct);
            await MutateAsync(() =>
            {
                // Trimmed once here so Home's own copy and the toasts built from
                // LocalReminderDefaults.PersonalizeTitle (which also trims) agree
                // on exactly what counts as a blank name.
                _recipientName = profile?.RecipientName?.Trim() ?? string.Empty;
                NextReminder = reminders
                    .Where(reminder => reminder.Enabled)
                    .Where(reminder => reminder.NextDueUtc is not null)
                    .OrderBy(reminder => reminder.NextDueUtc)
                    .FirstOrDefault();
                ActiveFocus = focus;

                Countdowns.Clear();
                foreach (var countdown in countdowns)
                {
                    Countdowns.Add(countdown);
                }

                CheckInSummary = summary;
                RecentCheckIns.Clear();
                foreach (var checkIn in CheckInSummary.Recent) RecentCheckIns.Add(checkIn);
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(PauseDescription));
                OnPropertyChanged(nameof(PetStateText));
                OnPropertyChanged(nameof(PetAnimationText));
                OnPropertyChanged(nameof(CheckInSummaryText));
                OnPropertyChanged(nameof(CheckInSectionHeading));
                OnPropertyChanged(nameof(YesterdayReflectionText));
                OnPropertyChanged(nameof(NextReminderText));
                OnPropertyChanged(nameof(NextCountdownText));
                // The shell caches pages/view models across visits: a stale pending
                // confirmation from a previous visit must not resurface on this one.
                PendingDeleteCountdown = null;
            }, ct);
        }, cancellationToken);
    }

    public Task PetAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.PresentOneShotPetAsync(
                new PetEvent.AmbientRequested("greeting"),
                "greeting",
                cancellationToken);
            OnPropertyChanged(nameof(PetPresentation));
            OnPropertyChanged(nameof(PetStateText));
            OnPropertyChanged(nameof(PetAnimationText));
        });

    public Task SetPauseAsync(
        PauseState state,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.ApplyPauseAsync(state, cancellationToken);
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseDescription));
            OnPropertyChanged(nameof(PetStateText));
        });

    public void SelectCountdown(Countdown? countdown)
    {
        SelectedCountdown = countdown;
        CountdownTitle = countdown?.Title ?? string.Empty;
        CountdownTargetUtc = countdown?.TargetUtc;
    }

    private void RequestDeleteCountdown(Countdown? countdown)
    {
        if (countdown is null)
        {
            ErrorMessage = SelectOneFirstMessage;
            return;
        }

        ErrorMessage = null;
        PendingDeleteCountdown = countdown;
    }

    public Task CreateCountdownAsync(CancellationToken cancellationToken = default) =>
        SaveCountdownAsync(cancellationToken);

    public Task SaveCountdownAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = CountdownTitle.Trim();
            if (title.Length == 0)
            {
                throw new ArgumentException("aiyo add a countdown title first", nameof(CountdownTitle));
            }

            var target = CountdownTargetUtc?.ToUniversalTime()
                ?? _context.Clock.UtcNow.AddDays(1).ToUniversalTime();
            var countdown = new Countdown(
                SelectedCountdown?.Id ?? Guid.NewGuid().ToString("N"),
                title,
                target,
                isAllDay: false,
                TimeZoneInfo.Local);
            await _context.Countdowns.SaveAsync(countdown, cancellationToken);
            await MutateAsync(() =>
            {
                var existing = Countdowns.FirstOrDefault(item => item.Id == countdown.Id);
                if (existing is null) Countdowns.Add(countdown);
                else Countdowns[Countdowns.IndexOf(existing)] = countdown;
                SelectCountdown(null);
                OnPropertyChanged(nameof(NextCountdownText));
            }, cancellationToken);
        }, "oki countdown saved");

    public Task DeleteCountdownAsync(
        Countdown? countdown,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(countdown);
            await _context.Countdowns.DeleteAsync(countdown.Id, cancellationToken);
            await MutateAsync(() =>
            {
                var existing = Countdowns.FirstOrDefault(item => item.Id == countdown.Id);
                if (existing is not null) Countdowns.Remove(existing);
                if (SelectedCountdown?.Id == countdown.Id) SelectCountdown(null);
                if (PendingDeleteCountdown?.Id == countdown.Id) PendingDeleteCountdown = null;
                OnPropertyChanged(nameof(NextCountdownText));
            }, cancellationToken);
        }, "okkk countdown deleted le");

    public Task RecordCheckInAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.CheckInService.RecordAsync(
                SelectedMood,
                CheckInNote,
                cancellationToken);
            var summary = await _context.CheckInService.SummarizeAsync(7, cancellationToken);
            await MutateAsync(() =>
            {
                CheckInSummary = summary;
                RecentCheckIns.Clear();
                foreach (var checkIn in CheckInSummary.Recent) RecentCheckIns.Add(checkIn);
                CheckInNote = null;
                OnPropertyChanged(nameof(CheckInSummaryText));
            }, cancellationToken);
        }, "oki noted mwamwa");

    /// <summary>Shares FeatureViewModelBase's exception-to-copy mapping with
    /// HomePage's code-behind click handlers, which run outside any
    /// RunAsync call and would otherwise surface raw exception.Message.</summary>
    public static string DescribeError(Exception exception) => ToUserMessage(exception);
}
