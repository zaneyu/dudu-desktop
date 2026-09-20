using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class TasksFocusViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private TaskItem? _selectedTask;
    private TaskItem? _pendingDeleteTask;
    private FocusSnapshot? _activeFocus;
    private string _title = string.Empty;
    private string? _notes;
    private DateTimeOffset? _dueUtc;
    private int _selectedDurationMinutes = 25;
    private int _customDurationMinutes = 30;
    private bool _focusExpirySubscribed;

    public TasksFocusViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        SaveTaskCommand = new AsyncRelayCommand((CancellationToken ct) => SaveTaskAsync(ct));
        SelectTaskCommand = new RelayCommand<TaskItem>(SelectTask);
        CompleteTaskCommand = new AsyncRelayCommand<TaskItem>((item, ct) => CompleteTaskAsync(item, ct));
        DeleteTaskCommand = new AsyncRelayCommand<TaskItem>((item, ct) => DeleteTaskAsync(item, ct));
        RequestDeleteTaskCommand = new RelayCommand<TaskItem>(RequestDeleteTask);
        CancelDeleteTaskCommand = new RelayCommand(() => PendingDeleteTask = null);
        StartFocusCommand = new AsyncRelayCommand((CancellationToken ct) => StartFocusAsync(ct));
        PauseFocusCommand = new AsyncRelayCommand((CancellationToken ct) => PauseFocusAsync(ct));
        ResumeFocusCommand = new AsyncRelayCommand((CancellationToken ct) => ResumeFocusAsync(ct));
        ExtendFocusCommand = new AsyncRelayCommand((CancellationToken ct) => ExtendFocusAsync(ct));
        EndFocusCommand = new AsyncRelayCommand((CancellationToken ct) => EndFocusAsync(ct));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveTaskCommand { get; }
    public IRelayCommand<TaskItem> SelectTaskCommand { get; }
    public IAsyncRelayCommand<TaskItem> CompleteTaskCommand { get; }
    public IAsyncRelayCommand<TaskItem> DeleteTaskCommand { get; }
    public IRelayCommand<TaskItem> RequestDeleteTaskCommand { get; }
    public IRelayCommand CancelDeleteTaskCommand { get; }
    public IAsyncRelayCommand StartFocusCommand { get; }
    public IAsyncRelayCommand PauseFocusCommand { get; }
    public IAsyncRelayCommand ResumeFocusCommand { get; }
    public IAsyncRelayCommand ExtendFocusCommand { get; }
    public IAsyncRelayCommand EndFocusCommand { get; }

    public ObservableCollection<TaskItem> ActiveTasks { get; } = [];
    public ObservableCollection<TaskItem> CompletedTasks { get; } = [];
    public ObservableCollection<FocusHistoryEntry> FocusHistory { get; } = [];
    public IReadOnlyList<int> FocusPresets { get; } = [15, 25, 45, 60];

    public TaskItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value)) return;
            // The pending confirmation names a specific task; once the user looks at
            // something else, confirming should not delete the task they left behind.
            if (PendingDeleteTask is not null && PendingDeleteTask.Id != value?.Id)
            {
                PendingDeleteTask = null;
            }
        }
    }
    public TaskItem? PendingDeleteTask
    {
        get => _pendingDeleteTask;
        private set
        {
            if (SetProperty(ref _pendingDeleteTask, value))
            {
                OnPropertyChanged(nameof(IsConfirmingDeleteTask));
                OnPropertyChanged(nameof(DeleteTaskPrompt));
            }
        }
    }
    public bool IsConfirmingDeleteTask => PendingDeleteTask is not null;
    public string? DeleteTaskPrompt => PendingDeleteTask is null
        ? null
        : $"delete \"{PendingDeleteTask.Title}\" for good? cannot undo";
    public FocusSnapshot? ActiveFocus
    {
        get => _activeFocus;
        private set
        {
            if (SetProperty(ref _activeFocus, value)) OnPropertyChanged(nameof(ActiveFocusText));
        }
    }
    public string ActiveFocusText => ActiveFocus is null
        ? "no focus running ah"
        : $"focus is {ActiveFocus.Status.ToString().ToLowerInvariant()} with {FormatDuration(ActiveFocus.Remaining)} remaining";
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
        await RunRefreshAsync(async ct =>
        {
            var active = await _context.Tasks.ListActiveAsync(ct);
            var completed = await _context.Tasks.ListCompletedAsync(ct);
            var focus = await _context.FocusService.GetCurrentAsync(ct);
            var history = await _context.FocusSessions.ListHistoryAsync(ct);
            await MutateAsync(() =>
            {
                ActiveTasks.Clear();
                foreach (var task in active) ActiveTasks.Add(task);
                CompletedTasks.Clear();
                foreach (var task in completed) CompletedTasks.Add(task);
                ActiveFocus = focus;
                FocusHistory.Clear();
                foreach (var session in history) FocusHistory.Add(ToHistoryEntry(session));
                OnPropertyChanged(nameof(IsFocusActive));
                // The shell caches pages/view models across visits: a stale pending
                // confirmation from a previous visit must not resurface on this one.
                PendingDeleteTask = null;
            }, ct);
        }, cancellationToken);
    }

    public Task SaveTaskAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var saved = SelectedTask is null
                ? await _context.TaskService.CreateAsync(Title, Normalize(Notes), DueUtc, cancellationToken)
                : await _context.TaskService.UpdateAsync(SelectedTask.Id, Title, Normalize(Notes), DueUtc, cancellationToken);
            await MutateAsync(() =>
            {
                var existing = ActiveTasks.FirstOrDefault(item => item.Id == saved.Id);
                if (existing is not null) ActiveTasks[ActiveTasks.IndexOf(existing)] = saved;
                else ActiveTasks.Add(saved);
                SelectTask(null);
            }, cancellationToken);
        }, "oki task saved");

    public void SelectTask(TaskItem? task)
    {
        SelectedTask = task;
        Title = task?.Title ?? string.Empty;
        Notes = task?.Notes;
        DueUtc = task?.DueUtc;
    }

    private void RequestDeleteTask(TaskItem? task)
    {
        if (task is null)
        {
            ErrorMessage = SelectOneFirstMessage;
            return;
        }

        ErrorMessage = null;
        PendingDeleteTask = task;
    }

    public Task CompleteTaskAsync(TaskItem? task, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(task);
            var completed = await _context.TaskService.CompleteAsync(task.Id, cancellationToken);
            await MutateAsync(() =>
            {
                var active = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
                if (active is not null) ActiveTasks.Remove(active);
                CompletedTasks.Insert(0, completed);
                if (SelectedTask?.Id == task.Id) SelectedTask = null;
                if (PendingDeleteTask?.Id == task.Id) PendingDeleteTask = null;
            }, cancellationToken);
        }, "okkk task done");

    public Task DeleteTaskAsync(TaskItem? task, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(task);
            await _context.TaskService.DeleteAsync(task.Id, cancellationToken);
            await MutateAsync(() =>
            {
                var active = ActiveTasks.FirstOrDefault(item => item.Id == task.Id);
                if (active is not null) ActiveTasks.Remove(active);
                var completed = CompletedTasks.FirstOrDefault(item => item.Id == task.Id);
                if (completed is not null) CompletedTasks.Remove(completed);
                if (SelectedTask?.Id == task.Id) SelectTask(null);
                if (PendingDeleteTask?.Id == task.Id) PendingDeleteTask = null;
            }, cancellationToken);
        }, "otayyy task deleted le");

    public Task StartFocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () => await StartFocusCoreAsync(cancellationToken), "yayyy focus started");

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
        return snapshot;
    }

    public Task PauseFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync((id, token) => _context.FocusService.PauseAsync(id, token), "done le focus paused", cancellationToken);

    public Task ResumeFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync((id, token) => _context.FocusService.ResumeAsync(id, token), "can resume focus", cancellationToken);

    public Task ExtendFocusAsync(CancellationToken cancellationToken = default) =>
        RunFocusTransitionAsync(
            (id, token) => _context.FocusService.ExtendAsync(id, TimeSpan.FromMinutes(5), token),
            "oki extended by 5 min", cancellationToken);

    public Task EndFocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var focus = ActiveFocus ?? throw new InvalidOperationException("wait no focus running now");
            var ended = await _context.FocusService.EndAsync(focus.Id, cancellationToken);
            ActiveFocus = ended;
            await _context.PresentOneShotPetAsync(
                new PetEvent.FocusEnded(ended.Id.ToString("D")),
                "focus-end",
                cancellationToken);
            OnPropertyChanged(nameof(IsFocusActive));

            // FocusHistory is otherwise only populated by RefreshAsync (on
            // Page_Loaded), so ending a session here would leave the
            // on-screen history stale until she navigates away and back. The
            // session itself has already ended successfully above, so this
            // reload is best-effort and gets its own try/catch: a transient
            // failure here (e.g. reading history) must not turn a successful
            // end into a reported failure -- it just leaves the on-screen
            // history stale until the next refresh.
            try
            {
                var history = await _context.FocusSessions.ListHistoryAsync(cancellationToken);
                await MutateAsync(() =>
                {
                    FocusHistory.Clear();
                    foreach (var session in history) FocusHistory.Add(ToHistoryEntry(session));
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu focus history refresh failed: {0}", exception);
            }
        }, "good job rest rest abit");

    private Task RunFocusTransitionAsync(
        Func<Guid, CancellationToken, Task<FocusSnapshot>> transition,
        string successMessage,
        CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var focus = ActiveFocus ?? throw new InvalidOperationException("wait no focus running now");
            ActiveFocus = await transition(focus.Id, cancellationToken);
            OnPropertyChanged(nameof(IsFocusActive));
        }, successMessage);

    /// <summary>Subscribes to <see cref="Dudu.Core.Focus.FocusService.SessionExpired"/> so a
    /// session that expires naturally -- the background reminder tick calling
    /// <c>FocusService.CompleteExpiredAsync</c>, not a manual "end focus" -- refreshes this
    /// page immediately instead of leaving ActiveFocus/FocusHistory stale until she navigates
    /// away and back. Idempotent: SettingsWindow caches this page and re-attaches on every
    /// Loaded.</summary>
    public void AttachFocusExpiry()
    {
        if (_focusExpirySubscribed) return;
        _focusExpirySubscribed = true;
        _context.FocusService.SessionExpired += OnFocusSessionExpired;
    }

    public void DetachFocusExpiry()
    {
        if (!_focusExpirySubscribed) return;
        _focusExpirySubscribed = false;
        _context.FocusService.SessionExpired -= OnFocusSessionExpired;
    }

    // Raised from the background reminder tick thread, not the UI thread -- and
    // FocusService.CompleteExpiredAsync must never see an exception escape this handler,
    // since that would break the tick for every other subscriber (e.g. the pet). Fire and
    // forget: RefreshAsync marshals its own mutations through MutateAsync/UiDispatcher.
    private void OnFocusSessionExpired(Guid focusId) => _ = RefreshAfterExpiryAsync();

    private async Task RefreshAfterExpiryAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu focus expiry refresh failed: {0}", exception);
        }
    }

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

    /// <summary>Projects a raw FocusSession into display-ready text: the enum
    /// name and UTC timestamp are framework/storage details, not something a
    /// non-technical user should read in the history list.</summary>
    private static FocusHistoryEntry ToHistoryEntry(FocusSession session) =>
        new(FormatStatus(session.Status), session.StartedUtc.ToLocalTime().ToString("g"));

    private static string FormatStatus(FocusStatus status) => status switch
    {
        FocusStatus.Running => "running",
        FocusStatus.Paused => "paused",
        FocusStatus.Completed => "completed",
        FocusStatus.EndedEarly => "ended early",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>Display-ready projection of a FocusSession for the focus-history
/// list, so the page never binds directly to the raw enum/UTC fields.</summary>
public sealed record FocusHistoryEntry(string StatusText, string StartedText);
