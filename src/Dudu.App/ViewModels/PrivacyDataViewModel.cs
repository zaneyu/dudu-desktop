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
        ? "Confirmation required"
        : "Confirm this action";
    public string ConfirmationMessage => PendingConfirmation switch
    {
        PrivacyConfirmationAction.Restore => "Restore the newest valid backup? Current local data will be replaced, and Dudu must be restarted afterward.",
        PrivacyConfirmationAction.DeleteLocal => "Permanently delete this PC's profile, preferences, activity history, notes, encrypted envelopes, asset metadata, backups, and pairing secrets? This cannot be undone.",
        PrivacyConfirmationAction.DeleteRemote => "Ask the pairing relay to permanently delete this device's remote data and revoke its sessions? Local data on this PC will remain.",
        _ => "Choose Restore or a Delete action above, then review the exact effect here before confirming.",
    };
    public string ConfirmationButtonText => PendingConfirmation switch
    {
        PrivacyConfirmationAction.Restore => "Confirm restore",
        PrivacyConfirmationAction.DeleteLocal => "Confirm local deletion",
        PrivacyConfirmationAction.DeleteRemote => "Confirm remote deletion",
        _ => "Confirm",
    };
    public ObservableCollection<StoredFieldDescription> StoredFields { get; } =
    [
        Local("Profile", "Recipient name and whether setup is complete.", "Until local deletion.", true),
        Local("Preferences", "Theme, motion, quiet hours, note limit, startup, layering, fullscreen, and reminder defaults.", "Until changed or locally deleted.", true),
        Local("Pet placement", "Monitor identifier, normalized position, and scale.", "Until changed or locally deleted.", true),
        Local("Reminders and occurrences", "Titles, details, schedules, time zone, quiet-hours behavior, next due and snooze state, plus completion timestamps.", "Until you delete them or delete local data.", true),
        Local("Tasks", "Task text, notes, due dates, completion state, and created, updated, and completed times.", "Until you delete them or delete local data.", true),
        Local("Focus history", "Optional task link, start/end times, paused remainder, duration, and completion status.", "Until local deletion.", true),
        Local("Countdowns", "Titles, target date or time, all-day choice, and display time zone.", "Until you delete them or delete local data.", true),
        Local("Local notes", "Bundled defaults and notes you save in the local love-note jar.", "Until you delete a note or delete local data.", true),
        Local("Local-note display history", "Which note was shown, when, local date, and whether Dudu chose it without a request.", "Used to limit repetition; removed by local deletion.", true),
        new("Remote envelopes", "Encrypted ciphertext and delivery metadata while a remote note waits to be opened; plaintext exists only in memory unless you save it.", "Encrypted transport data arrives from the pairing relay; reveal does not send a read receipt.", "Until reveal/acknowledgment, deletion, or the relay's 30-day undelivered limit.", "Pending ciphertext is backed up; local deletion removes it. Remote deletion separately asks the relay to erase its copy."),
        Local("Processed message IDs", "Opaque message IDs and processing timestamps used to prevent duplicate remote-note delivery.", "Retained locally for deduplication until local deletion.", true),
        Local("Check-ins", "Mood choice, optional note, and timestamp entered manually; Dudu does not infer mood.", "Until local deletion.", true),
        new("Pairing and sessions", "Machine-bound keys plus opaque pairing, expiry, revocation, and sender-session metadata; no browser history or identity profile.", "Pairing metadata and encrypted traffic use the relay while pairing is enabled.", "Until revocation, remote deletion, or local deletion.", "Machine-bound secrets are never in portable database backups. Restore on another PC requires pairing again."),
        Local("Imported asset-pack metadata", "Pack ID, version, local manifest path, attribution, private-use flag, and selected state.", "Until the pack or local data is deleted.", true),
        Local("Notification state", "Delivery preferences, quiet-hour deferrals, next-due and snooze state; no unrelated notification content or analytics.", "Until the related reminder or local data is deleted.", true),
    ];

    private static StoredFieldDescription Local(
        string name,
        string description,
        string retention,
        bool includedInBackup) => new(
            name,
            description,
            "Does not leave this PC.",
            retention,
            includedInBackup
                ? "Included in local database backups; removed from the live database and backups by local deletion."
                : "Not included in backups; removed by local deletion.");

    public Task BackupAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.BackupAsync(cancellationToken), "Backup created on this PC.");

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.RestoreAsync(cancellationToken), "Restore completed. Restart Dudu if requested.");

    public Task DeleteLocalDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), "Local data deletion requested.");

    public Task DeleteRemoteDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken), "Remote device deletion requested.");

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        var action = PendingConfirmation;
        if (action == PrivacyConfirmationAction.None) return;

        var succeeded = action switch
        {
            PrivacyConfirmationAction.Restore => await RunAsync(
                () => _context.RestoreAsync(cancellationToken),
                "Restore completed. Restart Dudu before making more changes."),
            PrivacyConfirmationAction.DeleteLocal => await RunAsync(
                () => _context.DeleteLocalDataAsync(cancellationToken),
                "Local data deleted. Restart Dudu to begin clean setup."),
            PrivacyConfirmationAction.DeleteRemote => await RunAsync(
                () => _context.DeleteRemoteDataAsync(cancellationToken),
                "Remote device data deleted and sessions revoked."),
            _ => false,
        };
        if (succeeded) PendingConfirmation = PrivacyConfirmationAction.None;
    }

    private void RequestConfirmation(PrivacyConfirmationAction action) =>
        PendingConfirmation = action;

    private void CancelConfirmation() => PendingConfirmation = PrivacyConfirmationAction.None;
}
