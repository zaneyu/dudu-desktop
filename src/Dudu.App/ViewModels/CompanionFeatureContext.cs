using Dudu.App.System;
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
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private Preferences _currentPreferences;
    public CompanionFeatureContext(
        IClock clock,
        Preferences initialPreferences,
        IPreferencesRepository preferences,
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
        PetStateMachine pet,
        Func<Preferences, CancellationToken, Task>? applyPreferencesAsync = null,
        Func<PetPlacement, CancellationToken, Task>? applyPlacementAsync = null,
        Func<bool, CancellationToken, Task>? setUserVisibleAsync = null,
        Func<PauseState>? getPauseState = null,
        Func<PauseState, CancellationToken, Task>? applyPauseAsync = null,
        Func<PetEvent, CancellationToken, Task>? presentPetAsync = null,
        Func<RemoteEnvelope, CancellationToken, Task<string>>? revealRemoteNoteAsync = null,
        Func<CancellationToken, Task>? backupAsync = null,
        Func<CancellationToken, Task>? restoreAsync = null,
        Func<CancellationToken, Task>? deleteLocalDataAsync = null,
        Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
        Func<string?, CancellationToken, Task>? applyOutfitAsync = null,
        Func<string, CancellationToken, Task>? setGlobalShortcutAsync = null)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _currentPreferences = initialPreferences ?? throw new ArgumentNullException(nameof(initialPreferences));
        Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
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
        Pet = pet ?? throw new ArgumentNullException(nameof(pet));
        ApplyPreferencesAsync = applyPreferencesAsync ?? ((_, _) => Task.CompletedTask);
        ApplyPlacementAsync = applyPlacementAsync ?? ((_, _) => Task.CompletedTask);
        SetUserVisibleAsync = setUserVisibleAsync ?? ((_, _) => Task.CompletedTask);
        GetPauseState = getPauseState ?? (() => PauseState.None);
        ApplyPauseAsync = applyPauseAsync ?? ((_, _) => Task.CompletedTask);
        PresentPetAsync = presentPetAsync ?? ((petEvent, _) =>
        {
            Pet.Handle(petEvent);
            return Task.CompletedTask;
        });
        RevealRemoteNoteAsync = revealRemoteNoteAsync ?? ((_, _) =>
            Task.FromException<string>(new NotSupportedException(
                "Remote note reveal is unavailable while the relay is offline.")));
        BackupAsync = backupAsync ?? ((_) => Task.FromException(
            new NotSupportedException("Local backup is not available yet.")));
        RestoreAsync = restoreAsync ?? ((_) => Task.FromException(
            new NotSupportedException("Local restore is not available yet.")));
        DeleteLocalDataAsync = deleteLocalDataAsync ?? ((_) => Task.FromException(
            new NotSupportedException("Local data deletion is not available yet.")));
        DeleteRemoteDataAsync = deleteRemoteDataAsync ?? ((_) => Task.FromException(
            new NotSupportedException("Remote data deletion is not available yet.")));
        ApplyOutfitAsync = applyOutfitAsync ?? ((_, _) => Task.FromException(
            new NotSupportedException("Outfits are not available in this companion runtime.")));
        SetGlobalShortcutAsync = setGlobalShortcutAsync ?? ((_, _) => Task.FromException(
            new NotSupportedException("Global shortcuts are not available in this companion runtime.")));
    }

    public IClock Clock { get; }
    [Obsolete("Use CurrentPreferences or UpdatePreferencesAsync so concurrent pages do not overwrite each other.")]
    public Preferences InitialPreferences => CurrentPreferences;
    public Preferences CurrentPreferences => Volatile.Read(ref _currentPreferences);
    public IPreferencesRepository Preferences { get; }
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
    public PetStateMachine Pet { get; }
    public Func<Preferences, CancellationToken, Task> ApplyPreferencesAsync { get; }
    public Func<PetPlacement, CancellationToken, Task> ApplyPlacementAsync { get; }
    public Func<bool, CancellationToken, Task> SetUserVisibleAsync { get; }
    public Func<PauseState> GetPauseState { get; }
    public Func<PauseState, CancellationToken, Task> ApplyPauseAsync { get; }
    public Func<PetEvent, CancellationToken, Task> PresentPetAsync { get; }
    public Func<RemoteEnvelope, CancellationToken, Task<string>> RevealRemoteNoteAsync { get; }
    public Func<CancellationToken, Task> BackupAsync { get; }
    public Func<CancellationToken, Task> RestoreAsync { get; }
    public Func<CancellationToken, Task> DeleteLocalDataAsync { get; }
    public Func<CancellationToken, Task> DeleteRemoteDataAsync { get; }
    public Func<string?, CancellationToken, Task> ApplyOutfitAsync { get; }
    public Func<string, CancellationToken, Task> SetGlobalShortcutAsync { get; }

    /// <summary>Serializes preference read/modify/write operations across
    /// feature pages. The shared current snapshot changes only after the
    /// repository write has succeeded.</summary>
    public async Task<Preferences> UpdatePreferencesAsync(
        Func<Preferences, Preferences> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            var updated = update(_currentPreferences);
            await Preferences.SaveAsync(updated, cancellationToken);
            Volatile.Write(ref _currentPreferences, updated);
            await ApplyPreferencesAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _preferencesGate.Release();
        }
    }
}

public abstract class FeatureViewModelBase : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

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

    protected static string ToUserMessage(Exception exception) =>
        exception switch
        {
            NotSupportedException => exception.Message,
            ArgumentException => exception.Message,
            KeyNotFoundException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => "Dudu could not finish that action. Try again.",
        };
}
