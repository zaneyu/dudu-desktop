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
        new("Preferences", "Theme, quiet hours, reminders, startup, and privacy choices."),
        new("Pet placement", "Monitor identity, position, scale, outfit, and layering choice."),
        new("Reminders", "Titles, schedules, recurrence, and completion occurrences."),
        new("Tasks and focus", "Task text, due dates, focus timing, and local history."),
        new("Local notes", "Notes you choose to keep in the local love-note jar."),
        new("Remote envelopes", "Encrypted message envelopes while they wait to be opened."),
        new("Check-ins", "Optional manual check-ins stored only on this PC."),
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
