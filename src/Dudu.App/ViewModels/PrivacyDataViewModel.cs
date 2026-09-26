using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dudu.App.ViewModels;

public sealed record StoredFieldDescription(
    string Name,
    string Description,
    string LeavesThisPc,
    string Retention,
    string BackupAndDeletion);

public enum PrivacyConfirmationAction
{
    None,
    Restore,
    DeleteLocal,
    DeleteRemote,
}

public sealed class PrivacyDataViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private PrivacyConfirmationAction _pendingConfirmation;

    public PrivacyDataViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        // Every data action is gated on !IsBusy: a backup still copying the database must not
        // race a confirmed local deletion or restore, and the pending confirmation must not be
        // swapped out from under the action that is running (its success would then silently
        // disarm the newly requested one).
        BackupCommand = new AsyncRelayCommand(() => BackupAsync(CancellationToken.None), () => !IsBusy);
        RequestRestoreCommand = new RelayCommand(
            () => RequestConfirmation(PrivacyConfirmationAction.Restore),
            () => !IsBusy);
        RequestDeleteLocalDataCommand = new RelayCommand(
            () => RequestConfirmation(PrivacyConfirmationAction.DeleteLocal),
            () => !IsBusy);
        RequestDeleteRemoteDataCommand = new RelayCommand(
            () => RequestConfirmation(PrivacyConfirmationAction.DeleteRemote),
            () => !IsBusy);
        ConfirmCommand = new AsyncRelayCommand(
            () => ConfirmAsync(CancellationToken.None),
            () => PendingConfirmation != PrivacyConfirmationAction.None && !IsBusy);
        CancelConfirmationCommand = new RelayCommand(
            CancelConfirmation,
            () => PendingConfirmation != PrivacyConfirmationAction.None && !IsBusy);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsBusy)) return;
        BackupCommand.NotifyCanExecuteChanged();
        RequestRestoreCommand.NotifyCanExecuteChanged();
        RequestDeleteLocalDataCommand.NotifyCanExecuteChanged();
        RequestDeleteRemoteDataCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        CancelConfirmationCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand BackupCommand { get; }
    public IRelayCommand RequestRestoreCommand { get; }
    public IRelayCommand RequestDeleteLocalDataCommand { get; }
    public IRelayCommand RequestDeleteRemoteDataCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelConfirmationCommand { get; }
    public PrivacyConfirmationAction PendingConfirmation
    {
        get => _pendingConfirmation;
        private set
        {
            if (!SetProperty(ref _pendingConfirmation, value)) return;
            OnPropertyChanged(nameof(ConfirmationTitle));
            OnPropertyChanged(nameof(ConfirmationMessage));
            OnPropertyChanged(nameof(ConfirmationButtonText));
            OnPropertyChanged(nameof(HasPendingConfirmation));
            ConfirmCommand.NotifyCanExecuteChanged();
            CancelConfirmationCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Drives the confirmation panel's visibility: with nothing pending it used to
    /// sit on screen permanently as a warning box with a disabled confirm button.</summary>
    public bool HasPendingConfirmation => PendingConfirmation != PrivacyConfirmationAction.None;
    public string ConfirmationTitle => PendingConfirmation == PrivacyConfirmationAction.None
        ? "confirmation needed"
        : "confirm this action";
    public string ConfirmationMessage => PendingConfirmation switch
    {
        // Restoring never restarted anything, yet this used to promise "then dudu restarts".
        // Say what actually happens: the newest backup replaces what is on this pc now.
        PrivacyConfirmationAction.Restore => "replace data on this pc with latest backup changes since then are lost",
        PrivacyConfirmationAction.DeleteLocal => "delete all data here forever cannot undo",
        PrivacyConfirmationAction.DeleteRemote => "delete remote data and sessions local stays",
        _ => "pick an action above to see effect",
    };
    public string ConfirmationButtonText => PendingConfirmation switch
    {
        PrivacyConfirmationAction.Restore => "confirm restore",
        PrivacyConfirmationAction.DeleteLocal => "confirm local deletion",
        PrivacyConfirmationAction.DeleteRemote => "confirm remote deletion",
        _ => "confirm",
    };
    public ObservableCollection<StoredFieldDescription> StoredFields { get; } =
    [
        Local("profile", "recipient name and whether setup is complete", "until local deletion", true),
        Local("preferences", "theme, motion, quiet hours, note limit, startup, layering, fullscreen, and reminder defaults", "until changed or locally deleted", true),
        Local("pet placement", "monitor identifier, normalized position, and scale", "until changed or locally deleted", true),
        Local("reminders and occurrences", "titles, schedules, quiet-hours behavior, next due, snooze state, completion times", "until u delete them or delete local data", true),
        Local("tasks", "task text, due dates, completion state, timestamps", "until u delete them or delete local data", true),
        Local("focus history", "optional task link, start/end times, paused remainder, duration, and completion status", "until local deletion", true),
        Local("countdowns", "titles, target date or time, all-day choice, and display time zone", "until u delete them or delete local data", true),
        Local("local notes", "bundled defaults and notes u save in local note jar", "until u delete a note or delete local data", true),
        Local("local-note display history", "which note shown, when, and if dudu picked it unprompted", "used to limit repetition removed by local deletion", true),
        new("remote envelopes", "encrypted ciphertext until note opened plaintext only in memory unless u save", "encrypted data passes through pairing relay reveal sends no read receipt", "until reveal/acknowledgment, deletion, or the relays 30-day undelivered limit", "backed up locally local deletion removes it remote deletion erases relay copy"),
        Local("processed message ids", "opaque message ids and processing timestamps used to prevent duplicate remote-note delivery", "retained locally for deduplication until local deletion", true),
        Local("check-ins", "mood choice, optional note, timestamp entered manually dudu never infers mood", "until local deletion", true),
        new("pairing and sessions", "machine-bound keys plus pairing, expiry, revocation, sender-session metadata no browser history", "pairing metadata and encrypted traffic use the relay while pairing is enabled", "until revocation, remote deletion, or local deletion", "machine-bound secrets never in backups restoring on another pc needs pairing again"),
        Local("imported asset-pack metadata", "pack id, version, local manifest path, attribution, private-use flag, and selected state", "until the pack or local data is deleted", true),
        Local("notification state", "delivery preferences, quiet-hour deferrals, next-due and snooze state nothing else tracked", "until the related reminder or local data is deleted", true),
    ];

    private static StoredFieldDescription Local(
        string name,
        string description,
        string retention,
        bool includedInBackup) => new(
            name,
            description,
            "does not leave this pc",
            retention,
            includedInBackup
                ? "included in backups removed from database and backups on local deletion"
                : "not included in backups removed by local deletion");

    public Task BackupAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.BackupAsync(cancellationToken), "oki backup created on this pc");

    private const string RestoredMessage = "okkk backup restored restart dudu if something looks off";

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.RestoreAsync(cancellationToken), RestoredMessage);

    public Task DeleteLocalDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), "otayyy local data deletion started");

    public Task DeleteRemoteDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken), "yayyy remote deletion started");

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        var action = PendingConfirmation;
        if (action == PrivacyConfirmationAction.None) return;

        var succeeded = action switch
        {
            PrivacyConfirmationAction.Restore => await RunAsync(
                () => _context.RestoreAsync(cancellationToken),
                RestoredMessage),
            PrivacyConfirmationAction.DeleteLocal => await RunAsync(
                () => _context.DeleteLocalDataAsync(cancellationToken),
                "can data deleted le restart dudu fresh"),
            PrivacyConfirmationAction.DeleteRemote => await RunAsync(
                () => _context.DeleteRemoteDataAsync(cancellationToken),
                "oki remote data deleted sessions revoked le"),
            _ => false,
        };
        if (succeeded) PendingConfirmation = PrivacyConfirmationAction.None;
    }

    /// <summary>Called when the (cached) page is shown again. A confirmation armed on an
    /// earlier visit -- "delete all data here forever" requested, then she navigated away --
    /// must not still be waiting one click from running, and an old visit's result line must
    /// not read as news. Leaves a running action's state alone.</summary>
    public void ResetTransientState()
    {
        if (IsBusy) return;
        PendingConfirmation = PrivacyConfirmationAction.None;
        ErrorMessage = null;
        StatusMessage = null;
    }

    // A result line from an earlier, different action must not linger next to a new
    // confirmation, or after cancelling one, as if it described the action now on screen.
    private void RequestConfirmation(PrivacyConfirmationAction action)
    {
        ErrorMessage = null;
        StatusMessage = null;
        PendingConfirmation = action;
    }

    private void CancelConfirmation()
    {
        ErrorMessage = null;
        StatusMessage = null;
        PendingConfirmation = PrivacyConfirmationAction.None;
    }
}
