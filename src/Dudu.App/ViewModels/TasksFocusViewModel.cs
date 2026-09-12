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
    private int _selectedDurationMinutes = 25;
    private int _customDurationMinutes = 30;

    public TasksFocusViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        SaveTaskCommand = new AsyncRelayCommand(() => SaveTaskAsync(CancellationToken.None));
        CompleteTaskCommand = new AsyncRelayCommand<TaskItem>(CompleteTaskAsync);
        StartFocusCommand = new AsyncRelayCommand(StartFocusAsync);
        PauseFocusCommand = new AsyncRelayCommand(PauseFocusAsync);
        ResumeFocusCommand = new AsyncRelayCommand(ResumeFocusAsync);
        ExtendFocusCommand = new AsyncRelayCommand(ExtendFocusAsync);
        EndFocusCommand = new AsyncRelayCommand(EndFocusAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveTaskCommand { get; }
    public IAsyncRelayCommand<TaskItem> CompleteTaskCommand { get; }
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
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
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
        });
    }

    public Task SaveTaskAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var saved = SelectedTask is null
                ? await _context.TaskService.CreateAsync(Title, Normalize(Notes), cancellationToken)
                : await _context.TaskService.UpdateAsync(SelectedTask.Id, Title, Normalize(Notes), cancellationToken);
            ReplaceTask(saved);
            SelectedTask = saved;
            Title = string.Empty;
            Notes = null;
        }, "Task saved.");

    public Task CompleteTaskAsync(TaskItem? task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunAsync(async () =>
        {
            var completed = await _context.TaskService.CompleteAsync(task.Id, cancellationToken);
            ActiveTasks.Remove(task);
            CompletedTasks.Insert(0, completed);
            if (SelectedTask?.Id == task.Id) SelectedTask = null;
        }, "Task completed.");
    }

    public Task StartFocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
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
        }, "Focus started.");

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
            await _context.PresentPetAsync(new PetEvent.FocusEnded(ended.Id.ToString("D")), cancellationToken);
            OnPropertyChanged(nameof(IsFocusActive));
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
        }, successMessage);

    private void ReplaceTask(TaskItem task)
    {
        var existing = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
        if (existing is not null) ActiveTasks[ActiveTasks.IndexOf(existing)] = task;
        else ActiveTasks.Add(task);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
