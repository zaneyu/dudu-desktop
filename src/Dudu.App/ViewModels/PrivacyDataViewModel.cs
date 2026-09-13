using System.Collections.ObjectModel;
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
        BackupCommand = new AsyncRelayCommand(() => BackupAsync(CancellationToken.None));
        RequestRestoreCommand = new RelayCommand(() => RequestConfirmation(PrivacyConfirmationAction.Restore));
        RequestDeleteLocalDataCommand = new RelayCommand(() => RequestConfirmation(PrivacyConfirmationAction.DeleteLocal));
        RequestDeleteRemoteDataCommand = new RelayCommand(() => RequestConfirmation(PrivacyConfirmationAction.DeleteRemote));
        ConfirmCommand = new AsyncRelayCommand(
            () => ConfirmAsync(CancellationToken.None),
            () => PendingConfirmation != PrivacyConfirmationAction.None);
        CancelConfirmationCommand = new RelayCommand(CancelConfirmation);
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
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }
    public string ConfirmationTitle => PendingConfirmation == PrivacyConfirmationAction.None
        ? "confirmation needed"
        : "confirm this action";
    public string ConfirmationMessage => PendingConfirmation switch
    {
        PrivacyConfirmationAction.Restore => "restore the newest valid backup? current local data will be replaced and dudu must be restarted after.",
        PrivacyConfirmationAction.DeleteLocal => "permanently delete this pcs profile, preferences, activity history, notes, encrypted envelopes, asset metadata, backups, and pairing secrets? this cannot be undone.",
        PrivacyConfirmationAction.DeleteRemote => "ask the pairing relay to permanently delete this devices remote data and revoke its sessions? local data on this pc will stay.",
        _ => "choose restore or a delete action above, then review the exact effect here before confirming",
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
        Local("profile", "recipient name and whether setup is complete.", "until local deletion.", true),
        Local("preferences", "theme, motion, quiet hours, note limit, startup, layering, fullscreen, and reminder defaults.", "until changed or locally deleted.", true),
        Local("pet placement", "monitor identifier, normalized position, and scale.", "until changed or locally deleted.", true),
        Local("reminders and occurrences", "titles, details, schedules, time zone, quiet-hours behavior, next due and snooze state, plus completion timestamps.", "until you delete them or delete local data.", true),
        Local("tasks", "task text, notes, due dates, completion state, and created, updated, and completed times.", "until you delete them or delete local data.", true),
        Local("focus history", "optional task link, start/end times, paused remainder, duration, and completion status.", "until local deletion.", true),
        Local("countdowns", "titles, target date or time, all-day choice, and display time zone.", "until you delete them or delete local data.", true),
        Local("local notes", "bundled defaults and notes you save in the local love-note jar.", "until you delete a note or delete local data.", true),
        Local("local-note display history", "which note was shown, when, local date, and whether dudu chose it without a request.", "used to limit repetition; removed by local deletion.", true),
        new("remote envelopes", "encrypted ciphertext and delivery metadata while a remote note waits to be opened; plaintext exists only in memory unless you save it.", "encrypted transport data arrives from the pairing relay; reveal does not send a read receipt.", "until reveal/acknowledgment, deletion, or the relays 30-day undelivered limit.", "pending ciphertext is backed up; local deletion removes it. remote deletion separately asks the relay to erase its copy."),
        Local("processed message ids", "opaque message ids and processing timestamps used to prevent duplicate remote-note delivery.", "retained locally for deduplication until local deletion.", true),
        Local("check-ins", "mood choice, optional note, and timestamp entered manually; dudu does not infer mood.", "until local deletion.", true),
        new("pairing and sessions", "machine-bound keys plus opaque pairing, expiry, revocation, and sender-session metadata; no browser history or identity profile.", "pairing metadata and encrypted traffic use the relay while pairing is enabled.", "until revocation, remote deletion, or local deletion.", "machine-bound secrets are never in portable database backups. restore on another pc requires pairing again."),
        Local("imported asset-pack metadata", "pack id, version, local manifest path, attribution, private-use flag, and selected state.", "until the pack or local data is deleted.", true),
        Local("notification state", "delivery preferences, quiet-hour deferrals, next-due and snooze state; no unrelated notification content or analytics.", "until the related reminder or local data is deleted.", true),
    ];

    private static StoredFieldDescription Local(
        string name,
        string description,
        string retention,
        bool includedInBackup) => new(
            name,
            description,
            "does not leave this pc.",
            retention,
            includedInBackup
                ? "included in local database backups; removed from the live database and backups by local deletion."
                : "not included in backups; removed by local deletion.");

    public Task BackupAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.BackupAsync(cancellationToken), "oki backup created on this pc");

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.RestoreAsync(cancellationToken), "restore done restart dudu if it asks");

    public Task DeleteLocalDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), "local data deletion requested");

    public Task DeleteRemoteDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken), "remote device deletion requested");

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        var action = PendingConfirmation;
        if (action == PrivacyConfirmationAction.None) return;

        var succeeded = action switch
        {
            PrivacyConfirmationAction.Restore => await RunAsync(
                () => _context.RestoreAsync(cancellationToken),
                "restore done restart dudu before you do anything else"),
            PrivacyConfirmationAction.DeleteLocal => await RunAsync(
                () => _context.DeleteLocalDataAsync(cancellationToken),
                "local data deleted le restart dudu for a clean setup"),
            PrivacyConfirmationAction.DeleteRemote => await RunAsync(
                () => _context.DeleteRemoteDataAsync(cancellationToken),
                "remote device data deleted and sessions revoked le"),
            _ => false,
        };
        if (succeeded) PendingConfirmation = PrivacyConfirmationAction.None;
    }

    private void RequestConfirmation(PrivacyConfirmationAction action) =>
        PendingConfirmation = action;

    private void CancelConfirmation() => PendingConfirmation = PrivacyConfirmationAction.None;
}
