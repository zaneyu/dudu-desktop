using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Dudu.App.ViewModels;

public sealed record StoredFieldDescription(string Name, string Description);

public sealed class PrivacyDataViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;

    public PrivacyDataViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        BackupCommand = new AsyncRelayCommand(() => BackupAsync(CancellationToken.None));
        RestoreCommand = new AsyncRelayCommand(() => RestoreAsync(CancellationToken.None));
        DeleteLocalDataCommand = new AsyncRelayCommand(() => DeleteLocalDataAsync(CancellationToken.None));
        DeleteRemoteDataCommand = new AsyncRelayCommand(() => DeleteRemoteDataAsync(CancellationToken.None));
    }

    public IAsyncRelayCommand BackupCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand DeleteLocalDataCommand { get; }
    public IAsyncRelayCommand DeleteRemoteDataCommand { get; }
    public ObservableCollection<StoredFieldDescription> StoredFields { get; } =
    [
        new("Profile", "The name Dudu uses for you and whether setup is complete."),
        new("Preferences", "Theme, reduced motion, quiet hours, note limit, startup, layering, fullscreen, and reminder defaults."),
        new("Pet placement", "Monitor identity, normalized position, and scale for where Dudu sits."),
        new("Reminders", "Titles, details, schedules, selected weekdays, local time zone, quiet-hours behavior, enabled state, next due time, snooze state, and completion occurrences."),
        new("Tasks", "Task text, notes, due dates, completion state, and created, updated, and completed times."),
        new("Focus history", "Task linkage, start and end times, paused remainder, duration, and completion status."),
        new("Countdowns", "Titles, target date or time, all-day choice, and the local time zone used to display them."),
        new("Local notes", "Notes you choose to keep in the local love-note jar."),
        new("Remote envelopes", "Encrypted ciphertext and delivery metadata while a remote note waits to be opened; plaintext is not stored in the envelope."),
        new("Check-ins", "Optional mood, note, and timestamp that you enter manually; Dudu does not infer a mood."),
        new("Pairing and sessions", "Opaque pairing, code-expiry, revocation, and sender-session metadata while pairing is enabled; no browser history or identity profile."),
        new("Notifications", "Reminder delivery preferences, quiet-hours deferrals, next-due and snooze state; Dudu does not collect unrelated notification content or analytics."),
    ];

    public Task BackupAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.BackupAsync(cancellationToken), "Backup created on this PC.");

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.RestoreAsync(cancellationToken), "Restore completed. Restart Dudu if requested.");

    public Task DeleteLocalDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), "Local data deletion requested.");

    public Task DeleteRemoteDataAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken), "Remote device deletion requested.");
}
