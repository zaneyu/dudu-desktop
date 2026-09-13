using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.App.System;
using Dudu.Core.Countdowns;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class HomeViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private Reminder? _nextReminder;
    private FocusSnapshot? _activeFocus;
    private Countdown? _selectedCountdown;
    private CheckInSummary? _checkInSummary;
    private string _countdownTitle = string.Empty;
    private DateTimeOffset? _countdownTargetUtc;
    private MoodChoice _selectedMood = MoodChoice.Okay;
    private string? _checkInNote;

    public HomeViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        PetCommand = new AsyncRelayCommand(() => PetAsync(CancellationToken.None));
        PauseForOneHourCommand = new AsyncRelayCommand(() =>
            SetPauseAsync(PausePolicy.ForOneHour(_context.Clock.UtcNow), CancellationToken.None));
        ResumeCommand = new AsyncRelayCommand(() =>
            SetPauseAsync(PauseState.None, CancellationToken.None));
        CreateCountdownCommand = new AsyncRelayCommand(() =>
            SaveCountdownAsync(CancellationToken.None));
        SelectCountdownCommand = new RelayCommand<Countdown>(SelectCountdown);
        DeleteCountdownCommand = new AsyncRelayCommand<Countdown>(DeleteCountdownAsync);
        RecordCheckInCommand = new AsyncRelayCommand(() =>
            RecordCheckInAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PetCommand { get; }
    public IAsyncRelayCommand PauseForOneHourCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand CreateCountdownCommand { get; }
    public IRelayCommand<Countdown> SelectCountdownCommand { get; }
    public IAsyncRelayCommand<Countdown> DeleteCountdownCommand { get; }
    public IAsyncRelayCommand RecordCheckInCommand { get; }

    public ObservableCollection<Countdown> Countdowns { get; } = [];
    public ObservableCollection<MoodCheckIn> RecentCheckIns { get; } = [];

    public PetPresentation PetPresentation => _context.Pet.Current;

    public bool IsPaused => _context.GetPauseState().Mode != PauseMode.None;

    public string PauseDescription => _context.GetPauseState() switch
    {
        { Mode: PauseMode.OneHour } => "paused for one hour",
        { Mode: PauseMode.FiveMinutes } => "paused for five minutes",
        { Mode: PauseMode.UntilTomorrowAtSeven } => "paused until tomorrow morning",
        { Mode: PauseMode.UntilFullscreenEnds } => "paused until fullscreen work ends",
        { Mode: PauseMode.Indefinite } => "paused until you resume dudu",
        _ => "dudu is available",
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
        set => SetProperty(ref _selectedCountdown, value);
    }

    public CheckInSummary? CheckInSummary
    {
        get => _checkInSummary;
        private set
        {
            if (SetProperty(ref _checkInSummary, value)) OnPropertyChanged(nameof(CheckInSummaryText));
        }
    }

    public string NextReminderText => NextReminder is null
        ? "no upcoming reminders"
        : $"next reminder: {NextReminder.Title} at {NextReminder.NextDueUtc.ToLocalTime():g}";

    public string ActiveFocusText => ActiveFocus is null
        ? "no focus session active"
        : $"focus is {ActiveFocus.Status.ToString().ToLowerInvariant()} with {Math.Max(0, (int)Math.Ceiling(ActiveFocus.Remaining.TotalMinutes))} min remaining";

    public string PetStateText => IsPaused
        ? "dudu is paused"
        : $"dudu is {PetPresentation.State.ToString().ToLowerInvariant()}";

    public string PetAnimationText => $"current animation {PetPresentation.AnimationKey}";

    public string CheckInSummaryText => CheckInSummary is null
        ? "no recent check-ins"
        : $"{CheckInSummary.Recent.Count} optional check-in{(CheckInSummary.Recent.Count == 1 ? string.Empty : "s")} in the last 7 days";

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
        await RunAsync(async () =>
        {
            var reminders = await _context.Reminders.ListAsync(cancellationToken);
            NextReminder = reminders
                .Where(reminder => reminder.Enabled)
                .OrderBy(reminder => reminder.NextDueUtc)
                .FirstOrDefault();
            ActiveFocus = await _context.FocusService.GetCurrentAsync(cancellationToken);

            Countdowns.Clear();
            foreach (var countdown in await _context.Countdowns.ListAsync(cancellationToken))
            {
                Countdowns.Add(countdown);
            }

            CheckInSummary = await _context.CheckInService.SummarizeAsync(7, cancellationToken);
            RecentCheckIns.Clear();
            foreach (var checkIn in CheckInSummary.Recent) RecentCheckIns.Add(checkIn);
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseDescription));
            OnPropertyChanged(nameof(PetStateText));
            OnPropertyChanged(nameof(PetAnimationText));
            OnPropertyChanged(nameof(CheckInSummaryText));
        });
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

    public Task CreateCountdownAsync(CancellationToken cancellationToken = default) =>
        SaveCountdownAsync(cancellationToken);

    public Task SaveCountdownAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = CountdownTitle.Trim();
            if (title.Length == 0)
            {
                throw new ArgumentException("aiyo add a title for the countdown first", nameof(CountdownTitle));
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
            var existing = Countdowns.FirstOrDefault(item => item.Id == countdown.Id);
            if (existing is null) Countdowns.Add(countdown);
            else Countdowns[Countdowns.IndexOf(existing)] = countdown;
            SelectCountdown(null);
        }, "oki countdown saved");

    public Task DeleteCountdownAsync(
        Countdown? countdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(countdown);
        return RunAsync(async () =>
        {
            await _context.Countdowns.DeleteAsync(countdown.Id, cancellationToken);
            var existing = Countdowns.FirstOrDefault(item => item.Id == countdown.Id);
            if (existing is not null) Countdowns.Remove(existing);
            if (SelectedCountdown?.Id == countdown.Id) SelectCountdown(null);
        }, "countdown deleted le");
    }

    public Task RecordCheckInAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.CheckInService.RecordAsync(
                SelectedMood,
                CheckInNote,
                cancellationToken);
            CheckInSummary = await _context.CheckInService.SummarizeAsync(7, cancellationToken);
            RecentCheckIns.Clear();
            foreach (var checkIn in CheckInSummary.Recent) RecentCheckIns.Add(checkIn);
            CheckInNote = null;
            OnPropertyChanged(nameof(CheckInSummaryText));
        }, "oki check-in saved on this pc");
}
