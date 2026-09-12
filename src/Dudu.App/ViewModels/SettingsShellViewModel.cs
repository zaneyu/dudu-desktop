using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Dudu.App.ViewModels;

public sealed record SettingsDestination(string Tag, string Title, string AutomationId);

public sealed class SettingsShellViewModel : INotifyPropertyChanged
{
    private static readonly SettingsDestination[] DestinationValues =
    [
        new("home", "Home", "NavHome"),
        new("reminders", "Reminders", "NavReminders"),
        new("tasks", "Tasks and Focus", "NavTasksFocus"),
        new("notes", "Love Notes", "NavLoveNotes"),
        new("appearance", "Appearance", "NavAppearance"),
        new("connection", "Connection", "NavConnection"),
        new("privacy", "Privacy and Data", "NavPrivacy"),
    ];

    private string _currentDestination = "home";

    public SettingsShellViewModel()
    {
        Destinations = new ReadOnlyCollection<SettingsDestination>(DestinationValues);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SettingsDestination> Destinations { get; }
    public string CurrentDestination => _currentDestination;

    public bool Navigate(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Destinations.Any(item => string.Equals(item.Tag, destination, StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.Equals(_currentDestination, destination, StringComparison.Ordinal))
        {
            return true;
        }

        _currentDestination = destination;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDestination)));
        return true;
    }

    public SettingsDestination GetDestination(string tag) =>
        Destinations.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(tag));
}
