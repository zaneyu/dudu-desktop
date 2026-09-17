using Dudu.App.System;
using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Dudu.Core.Tasks;

namespace Dudu.App.ViewModels;

/// <summary>
/// Application-facing dependencies for the seven settings pages and the pet
/// action surface. The context keeps pages independent from the WinUI shell.
/// </summary>
public sealed class CompanionFeatureContext
{
    public CompanionFeatureContext(
        IClock clock,
        PreferenceMutationCoordinator preferenceMutations,
        IProfileRepository profiles,
        IPetPlacementRepository petPlacements,
        IReminderRepository reminders,
        IReminderWriter reminderWriter,
        ITaskRepository tasks,
        IFocusSessionRepository focusSessions,
        ILocalNoteRepository localNotes,
        IRemoteEnvelopeRepository remoteEnvelopes,
        ICountdownRepository countdowns,
        ICheckInRepository checkIns,
        CheckInService checkInService,
        TaskService taskService,
        FocusService focusService,
        LocalNoteSelector noteSelector,
        IPairingService pairing,
        ICompanionFeatureTransactions featureTransactions,
        PetStateMachine pet,
        Func<PetPlacement, CancellationToken, Task>? applyPlacementAsync = null,
        Func<bool, CancellationToken, Task>? setUserVisibleAsync = null,
        Func<PauseState>? getPauseState = null,
        Func<PauseState, CancellationToken, Task>? applyPauseAsync = null,
        Func<PetEvent, CancellationToken, Task>? presentPetAsync = null,
        Func<PetEvent, string, CancellationToken, Task>? presentOneShotPetAsync = null,
        Func<RemoteEnvelope, CancellationToken, Task<RevealedRemoteNote>>? revealRemoteNoteAsync = null,
        Func<CancellationToken, Task>? backupAsync = null,
        Func<CancellationToken, Task>? restoreAsync = null,
        Func<CancellationToken, Task>? deleteLocalDataAsync = null,
        Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
        Func<string?, CancellationToken, Task>? applyOutfitAsync = null,
        Func<string, CancellationToken, Task>? setGlobalShortcutAsync = null,
        Func<string, CancellationToken, Task>? dismissReminderNotificationAsync = null)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PreferenceMutations = preferenceMutations ?? throw new ArgumentNullException(nameof(preferenceMutations));
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        PetPlacements = petPlacements ?? throw new ArgumentNullException(nameof(petPlacements));
        Reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        ReminderWriter = reminderWriter ?? throw new ArgumentNullException(nameof(reminderWriter));
        Tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        FocusSessions = focusSessions ?? throw new ArgumentNullException(nameof(focusSessions));
        LocalNotes = localNotes ?? throw new ArgumentNullException(nameof(localNotes));
        RemoteEnvelopes = remoteEnvelopes ?? throw new ArgumentNullException(nameof(remoteEnvelopes));
        Countdowns = countdowns ?? throw new ArgumentNullException(nameof(countdowns));
        CheckIns = checkIns ?? throw new ArgumentNullException(nameof(checkIns));
        CheckInService = checkInService ?? throw new ArgumentNullException(nameof(checkInService));
        TaskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        FocusService = focusService ?? throw new ArgumentNullException(nameof(focusService));
        NoteSelector = noteSelector ?? throw new ArgumentNullException(nameof(noteSelector));
        Pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        FeatureTransactions = featureTransactions ?? throw new ArgumentNullException(nameof(featureTransactions));
        Pet = pet ?? throw new ArgumentNullException(nameof(pet));
        ApplyPlacementAsync = applyPlacementAsync ?? ((_, _) => Task.CompletedTask);
        SetUserVisibleAsync = setUserVisibleAsync ?? ((_, _) => Task.CompletedTask);
        GetPauseState = getPauseState ?? (() => PauseState.None);
        ApplyPauseAsync = applyPauseAsync ?? ((_, _) => Task.CompletedTask);
        PresentPetAsync = presentPetAsync ?? ((petEvent, _) =>
        {
            Pet.Handle(petEvent);
            return Task.CompletedTask;
        });
        PresentOneShotPetAsync = presentOneShotPetAsync ?? (async (petEvent, dismissalId, token) =>
        {
            await PresentPetAsync(petEvent, token);
            Pet.Handle(new PetEvent.PresentationAcknowledged());
            Pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
        });
        RevealRemoteNoteAsync = revealRemoteNoteAsync ?? ((_, _) =>
            Task.FromException<RevealedRemoteNote>(new NotSupportedException(
                "aiyo cant reveal notes relay offline")));
        BackupAsync = backupAsync ?? ((_) => Task.FromException(
            new NotSupportedException("oh no backup not ready yet")));
        RestoreAsync = async token =>
        {
            if (restoreAsync is null)
            {
                throw new NotSupportedException("cannot restore right now try later");
            }

            await PreferenceMutations.ExecuteAndReloadAsync(
                restoreAsync,
                Preferences.Default,
                token);
        };
        DeleteLocalDataAsync = async token =>
        {
            if (deleteLocalDataAsync is null)
            {
                throw new NotSupportedException("alala cant delete local data yet");
            }

            await PreferenceMutations.ExecuteAndReloadAsync(
                deleteLocalDataAsync,
                Preferences.Default,
                token);
        };
        DeleteRemoteDataAsync = deleteRemoteDataAsync ?? ((_) => Task.FromException(
            new NotSupportedException("wait cant delete remote data yet")));
        ApplyOutfitAsync = applyOutfitAsync ?? ((_, _) => Task.FromException(
            new NotSupportedException("aiyo outfits not ready yet")));
        SetGlobalShortcutAsync = setGlobalShortcutAsync ?? ((_, _) => Task.FromException(
            new NotSupportedException("oh no shortcuts not ready yet")));
        DismissReminderNotificationAsync = dismissReminderNotificationAsync ?? ((_, _) => Task.CompletedTask);
    }

    public IClock Clock { get; }
    [Obsolete("Use CurrentPreferences or UpdatePreferencesAsync so concurrent pages do not overwrite each other.")]
    public Preferences InitialPreferences => CurrentPreferences;
    public Preferences CurrentPreferences => PreferenceMutations.Current;
    public PreferenceMutationCoordinator PreferenceMutations { get; }
    public IProfileRepository Profiles { get; }
    public IPetPlacementRepository PetPlacements { get; }
    public IReminderRepository Reminders { get; }
    public IReminderWriter ReminderWriter { get; }
    public ITaskRepository Tasks { get; }
    public IFocusSessionRepository FocusSessions { get; }
    public ILocalNoteRepository LocalNotes { get; }
    public IRemoteEnvelopeRepository RemoteEnvelopes { get; }
    public ICountdownRepository Countdowns { get; }
    public ICheckInRepository CheckIns { get; }
    public CheckInService CheckInService { get; }
    public TaskService TaskService { get; }
    public FocusService FocusService { get; }
    public LocalNoteSelector NoteSelector { get; }
    public IPairingService Pairing { get; }
    public ICompanionFeatureTransactions FeatureTransactions { get; }
    public PetStateMachine Pet { get; }
    public Func<PetPlacement, CancellationToken, Task> ApplyPlacementAsync { get; }
    public Func<bool, CancellationToken, Task> SetUserVisibleAsync { get; }
    public Func<PauseState> GetPauseState { get; }
    public Func<PauseState, CancellationToken, Task> ApplyPauseAsync { get; }
    public Func<PetEvent, CancellationToken, Task> PresentPetAsync { get; }
    public Func<PetEvent, string, CancellationToken, Task> PresentOneShotPetAsync { get; }
    public Func<RemoteEnvelope, CancellationToken, Task<RevealedRemoteNote>> RevealRemoteNoteAsync { get; }
    public Func<CancellationToken, Task> BackupAsync { get; }
    public Func<CancellationToken, Task> RestoreAsync { get; }
    public Func<CancellationToken, Task> DeleteLocalDataAsync { get; }
    public Func<CancellationToken, Task> DeleteRemoteDataAsync { get; }
    public Func<string?, CancellationToken, Task> ApplyOutfitAsync { get; }
    public Func<string, CancellationToken, Task> SetGlobalShortcutAsync { get; }
    /// <summary>Best-effort removal of a reminder's toast after the user
    /// acknowledged it (Done or Snooze).</summary>
    public Func<string, CancellationToken, Task> DismissReminderNotificationAsync { get; }

    /// <summary>Serializes preference read/modify/write operations across
    /// feature pages. The shared current snapshot changes only after durable
    /// persistence and runtime application have both succeeded.</summary>
    public async Task<Preferences> UpdatePreferencesAsync(
        Func<Preferences, Preferences> update,
        CancellationToken cancellationToken = default) =>
        await PreferenceMutations.UpdateAsync(update, cancellationToken);

    public async Task<Preferences> UpdatePreferencesAndDefaultRemindersAsync(
        Func<Preferences, Preferences> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        IReadOnlyList<Reminder> previousDefaults = [];
        return await PreferenceMutations.UpdateTransactionalAsync(
            update,
            async (_, updated, token) =>
            {
                previousDefaults = (await Reminders.ListAsync(token))
                    .Where(reminder => reminder.Id is "default-hydration" or "default-break"
                        or Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId
                        or Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId)
                    .ToArray();
                await FeatureTransactions.SavePreferencesAndDefaultRemindersAsync(
                    updated,
                    Clock.UtcNow.ToUniversalTime(),
                    Clock.LocalTimeZone,
                    token);
            },
            async (previous, _, token) =>
            {
                await FeatureTransactions.RestorePreferencesAndDefaultRemindersAsync(
                    previous,
                    previousDefaults,
                    token);
            },
            applyRuntime: true,
            cancellationToken);
    }
}

public abstract class FeatureViewModelBase : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _refreshGeneration;

    /// <summary>Optional UI-thread marshaler for ObservableCollection
    /// mutation. Null (tests) runs inline; production sets an
    /// AwaitableUiDispatcher-backed delegate.</summary>
    public Func<Action, CancellationToken, Task>? UiDispatcher { get; set; }

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        protected set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    /// <summary>True once an error is set. Pages pair this with a status icon
    /// so meaning never rests on color alone.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    /// <summary>True once a success status is set. Pages pair this with a
    /// status icon so meaning never rests on color alone.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

    protected async Task<bool> RunAsync(
        Func<Task> operation,
        string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await operation();
            StatusMessage = successMessage;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = ToUserMessage(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected Task<bool> RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string? successMessage = null) =>
        RunAsync(() => operation(cancellationToken), successMessage);

    /// <summary>Serializes refresh work so overlapping page activations
    /// cannot interleave collection mutation, and lets the latest request
    /// win: a refresh superseded while it waited for the gate is skipped.
    /// Awaits deliberately keep the caller's context so bound properties and
    /// ObservableCollections are only touched on the UI thread.</summary>
    protected async Task RunRefreshAsync(
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        var generation = Interlocked.Increment(ref _refreshGeneration);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != Volatile.Read(ref _refreshGeneration))
            {
                return;
            }

            await RunAsync(() => refresh(cancellationToken));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Marshals ObservableCollection mutation onto the UI thread when
    /// a dispatcher is configured; runs inline otherwise (tests).</summary>
    protected async Task MutateAsync(Action mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var dispatcher = UiDispatcher;
        if (dispatcher is null)
        {
            mutation();
            return;
        }

        await dispatcher(mutation, cancellationToken);
    }

    protected static string ToUserMessage(Exception exception) =>
        exception switch
        {
            NotSupportedException => exception.Message,
            ArgumentException => exception.Message,
            KeyNotFoundException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => "cannot finish that try again",
        };
}
