using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class TasksFocusViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private TaskItem? _selectedTask;
    private FocusSnapshot? _activeFocus;
    private string _title = string.Empty;
    private string? _notes;
    private DateTimeOffset? _dueUtc;
    private int _selectedDurationMinutes = 25;
    private int _customDurationMinutes = 30;

    public TasksFocusViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        SaveTaskCommand = new AsyncRelayCommand(() => SaveTaskAsync(CancellationToken.None));
        SelectTaskCommand = new RelayCommand<TaskItem>(SelectTask);
        CompleteTaskCommand = new AsyncRelayCommand<TaskItem>(CompleteTaskAsync);
        DeleteTaskCommand = new AsyncRelayCommand<TaskItem>(DeleteTaskAsync);
        StartFocusCommand = new AsyncRelayCommand(StartFocusAsync);
        PauseFocusCommand = new AsyncRelayCommand(PauseFocusAsync);
        ResumeFocusCommand = new AsyncRelayCommand(ResumeFocusAsync);
        ExtendFocusCommand = new AsyncRelayCommand(ExtendFocusAsync);
        EndFocusCommand = new AsyncRelayCommand(EndFocusAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveTaskCommand { get; }
    public IRelayCommand<TaskItem> SelectTaskCommand { get; }
    public IAsyncRelayCommand<TaskItem> CompleteTaskCommand { get; }
    public IAsyncRelayCommand<TaskItem> DeleteTaskCommand { get; }
    public IAsyncRelayCommand StartFocusCommand { get; }
    public IAsyncRelayCommand PauseFocusCommand { get; }
    public IAsyncRelayCommand ResumeFocusCommand { get; }
    public IAsyncRelayCommand ExtendFocusCommand { get; }
    public IAsyncRelayCommand EndFocusCommand { get; }

    public ObservableCollection<TaskItem> ActiveTasks { get; } = [];
    public ObservableCollection<TaskItem> CompletedTasks { get; } = [];
    public ObservableCollection<FocusSession> FocusHistory { get; } = [];
    public IReadOnlyList<int> FocusPresets { get; } = [15, 25, 45, 60];

    public TaskItem? SelectedTask { get => _selectedTask; set => SetProperty(ref _selectedTask, value); }
    public FocusSnapshot? ActiveFocus { get => _activeFocus; private set => SetProperty(ref _activeFocus, value); }
    public string ActiveFocusText => ActiveFocus is null
        ? "No focus session is active."
        : $"Focus is {ActiveFocus.Status.ToString().ToLowerInvariant()} with {FormatDuration(ActiveFocus.Remaining)} remaining.";
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public DateTimeOffset? DueUtc
    {
        get => _dueUtc;
        set
        {
            if (SetProperty(ref _dueUtc, value?.ToUniversalTime())) OnPropertyChanged(nameof(DueText));
        }
    }
    public string DueText
    {
        get => DueUtc?.ToLocalTime().ToString("g") ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) DueUtc = null;
            else if (DateTimeOffset.TryParse(value, out var parsed)) DueUtc = parsed;
        }
    }
    public int SelectedDurationMinutes { get => _selectedDurationMinutes; set => SetProperty(ref _selectedDurationMinutes, value); }
    public int CustomDurationMinutes { get => _customDurationMinutes; set => SetProperty(ref _customDurationMinutes, Math.Clamp(value, 1, 240)); }
    public bool IsFocusActive => ActiveFocus is { Status: FocusStatus.Running or FocusStatus.Paused };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            ActiveTasks.Clear();
            foreach (var task in await _context.Tasks.ListActiveAsync(cancellationToken)) ActiveTasks.Add(task);
            CompletedTasks.Clear();
            foreach (var task in await _context.Tasks.ListCompletedAsync(cancellationToken)) CompletedTasks.Add(task);
            ActiveFocus = await _context.FocusService.GetCurrentAsync(cancellationToken);
            FocusHistory.Clear();
            foreach (var session in await _context.FocusSessions.ListHistoryAsync(cancellationToken)) FocusHistory.Add(session);
            OnPropertyChanged(nameof(IsFocusActive));
            OnPropertyChanged(nameof(ActiveFocusText));
        });
    }

    public Task SaveTaskAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var saved = SelectedTask is null
                ? await _context.TaskService.CreateAsync(Title, Normalize(Notes), DueUtc, cancellationToken)
                : await _context.TaskService.UpdateAsync(SelectedTask.Id, Title, Normalize(Notes), DueUtc, cancellationToken);
            ReplaceTask(saved);
            SelectTask(null);
        }, "Task saved.");

    public void SelectTask(TaskItem? task)
    {
        SelectedTask = task;
        Title = task?.Title ?? string.Empty;
        Notes = task?.Notes;
        DueUtc = task?.DueUtc;
    }

    public Task CompleteTaskAsync(TaskItem? task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunAsync(async () =>
        {
            var completed = await _context.TaskService.CompleteAsync(task.Id, cancellationToken);
            var active = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
            if (active is not null) ActiveTasks.Remove(active);
            CompletedTasks.Insert(0, completed);
            if (SelectedTask?.Id == task.Id) SelectedTask = null;
        }, "Task completed.");
    }

    public Task DeleteTaskAsync(TaskItem? task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunAsync(async () =>
        {
            await _context.TaskService.DeleteAsync(task.Id, cancellationToken);
            var active = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
            if (active is not null) ActiveTasks.Remove(active);
            var completed = CompletedTasks.FirstOrDefault(item => item.Id == task.Id);
            if (completed is not null) CompletedTasks.Remove(completed);
            if (SelectedTask?.Id == task.Id) SelectTask(null);
        }, "Task deleted.");
    }

    public Task StartFocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () => await StartFocusCoreAsync(cancellationToken), "Focus started.");

    /// <summary>Result-bearing start path for callers that must not navigate
    /// or dismiss their surface until the focus session is durable.</summary>
    public Task<FocusSnapshot> StartFocusOrThrowAsync(
        CancellationToken cancellationToken = default) =>
        StartFocusCoreAsync(cancellationToken);

    private async Task<FocusSnapshot> StartFocusCoreAsync(CancellationToken cancellationToken)
    {
        var minutes = SelectedDurationMinutes > 0 ? SelectedDurationMinutes : CustomDurationMinutes;
        if (SelectedDurationMinutes <= 0) minutes = CustomDurationMinutes;
        var snapshot = await _context.FocusService.StartAsync(
            SelectedTask?.Id,
            TimeSpan.FromMinutes(minutes),
            cancellationToken);
        ActiveFocus = snapshot;
        await _context.PresentPetAsync(new PetEvent.FocusStarted(snapshot.Id.ToString("D")), cancellationToken);
        OnPropertyChanged(nameof(IsFocusActive));
        OnPropertyChanged(nameof(ActiveFocusText));
        return snapshot;
    }

    public Task PauseFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync((id, token) => _context.FocusService.PauseAsync(id, token), "Focus paused.", cancellationToken);

    public Task ResumeFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync((id, token) => _context.FocusService.ResumeAsync(id, token), "Focus resumed.", cancellationToken);

    public Task ExtendFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync(
            (id, token) => _context.FocusService.ExtendAsync(id, TimeSpan.FromMinutes(5), token),
            "Focus extended by 5 minutes.", cancellationToken);

    public Task EndFocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var focus = ActiveFocus ?? throw new InvalidOperationException("There is no active focus session.");
            var ended = await _context.FocusService.EndAsync(focus.Id, cancellationToken);
            ActiveFocus = ended;
            await _context.PresentOneShotPetAsync(
                new PetEvent.FocusEnded(ended.Id.ToString("D")),
                "focus-end",
                cancellationToken);
            OnPropertyChanged(nameof(IsFocusActive));
            OnPropertyChanged(nameof(ActiveFocusText));
        }, "Focus ended.");

    private Task RunFocusTransitionAsync(
        Func<Guid, CancellationToken, Task<FocusSnapshot>> transition,
        string successMessage,
        CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var focus = ActiveFocus ?? throw new InvalidOperationException("There is no active focus session.");
            ActiveFocus = await transition(focus.Id, cancellationToken);
            OnPropertyChanged(nameof(IsFocusActive));
            OnPropertyChanged(nameof(ActiveFocusText));
        }, successMessage);

    private void ReplaceTask(TaskItem task)
    {
        var existing = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
        if (existing is not null) ActiveTasks[ActiveTasks.IndexOf(existing)] = task;
        else ActiveTasks.Add(task);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatDuration(TimeSpan duration)
    {
        var roundedMinutes = Math.Max(0, (int)Math.Ceiling(duration.TotalMinutes));
        return roundedMinutes == 1 ? "1 minute" : $"{roundedMinutes} minutes";
    }
}
