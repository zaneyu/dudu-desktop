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
            CreateCountdownAsync(CancellationToken.None));
        RecordCheckInCommand = new AsyncRelayCommand(() =>
            RecordCheckInAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PetCommand { get; }
    public IAsyncRelayCommand PauseForOneHourCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand CreateCountdownCommand { get; }
    public IAsyncRelayCommand RecordCheckInCommand { get; }

    public ObservableCollection<Countdown> Countdowns { get; } = [];

    public PetPresentation PetPresentation => _context.Pet.Current;

    public bool IsPaused => _context.GetPauseState().Mode != PauseMode.None;

    public string PauseDescription => _context.GetPauseState() switch
    {
        { Mode: PauseMode.OneHour } => "Paused for one hour.",
        { Mode: PauseMode.UntilTomorrowAtSeven } => "Paused until tomorrow morning.",
        { Mode: PauseMode.UntilFullscreenEnds } => "Paused until fullscreen work ends.",
        { Mode: PauseMode.Indefinite } => "Paused until you resume Dudu.",
        _ => "Dudu is available.",
    };

    public Reminder? NextReminder
    {
        get => _nextReminder;
        private set => SetProperty(ref _nextReminder, value);
    }

    public FocusSnapshot? ActiveFocus
    {
        get => _activeFocus;
        private set => SetProperty(ref _activeFocus, value);
    }

    public Countdown? SelectedCountdown
    {
        get => _selectedCountdown;
        set => SetProperty(ref _selectedCountdown, value);
    }

    public CheckInSummary? CheckInSummary
    {
        get => _checkInSummary;
        private set => SetProperty(ref _checkInSummary, value);
    }

    public string CountdownTitle
    {
        get => _countdownTitle;
        set => SetProperty(ref _countdownTitle, value);
    }

    public DateTimeOffset? CountdownTargetUtc
    {
        get => _countdownTargetUtc;
        set => SetProperty(ref _countdownTargetUtc, value);
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
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseDescription));
        });
    }

    public Task PetAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.PresentPetAsync(
                new PetEvent.AmbientRequested("wave"),
                cancellationToken);
            OnPropertyChanged(nameof(PetPresentation));
        });

    public Task SetPauseAsync(
        PauseState state,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.ApplyPauseAsync(state, cancellationToken);
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseDescription));
        });

    public Task CreateCountdownAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var title = CountdownTitle.Trim();
            if (title.Length == 0)
            {
                throw new ArgumentException("Add a title for the countdown.", nameof(CountdownTitle));
            }

            var target = CountdownTargetUtc?.ToUniversalTime()
                ?? _context.Clock.UtcNow.AddDays(1).ToUniversalTime();
            var countdown = new Countdown(
                Guid.NewGuid().ToString("N"),
                title,
                target,
                isAllDay: false,
                TimeZoneInfo.Local);
            await _context.Countdowns.SaveAsync(countdown, cancellationToken);
            Countdowns.Add(countdown);
            SelectedCountdown = countdown;
            CountdownTitle = string.Empty;
            CountdownTargetUtc = null;
        }, "Countdown saved.");

    public Task RecordCheckInAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.CheckInService.RecordAsync(
                SelectedMood,
                CheckInNote,
                cancellationToken);
            CheckInSummary = await _context.CheckInService.SummarizeAsync(7, cancellationToken);
            CheckInNote = null;
        }, "Check-in saved on this PC.");
}
