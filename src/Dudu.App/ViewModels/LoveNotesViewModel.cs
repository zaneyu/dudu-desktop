using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class LoveNotesViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private LocalLoveNote? _selectedNote;
    private RemoteEnvelope? _openedRemoteEnvelope;
    private string? _openedRemoteNoteText;
    private string _draftText = string.Empty;
    private bool _draftEnabled = true;

    public LoveNotesViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        SaveLocalNoteCommand = new AsyncRelayCommand(() => SaveLocalNoteAsync(CancellationToken.None));
        DeleteLocalNoteCommand = new AsyncRelayCommand<LocalLoveNote>(DeleteLocalNoteAsync);
        RevealRemoteNoteCommand = new AsyncRelayCommand<RemoteEnvelope>(RevealRemoteNoteAsync);
        SaveOpenedNoteCommand = new AsyncRelayCommand<object?>(SaveOpenedNoteAsync);
        ShowLocalNoteCommand = new AsyncRelayCommand(ShowLocalNoteAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveLocalNoteCommand { get; }
    public IAsyncRelayCommand<LocalLoveNote> DeleteLocalNoteCommand { get; }
    public IAsyncRelayCommand<RemoteEnvelope> RevealRemoteNoteCommand { get; }
    public IAsyncRelayCommand<object?> SaveOpenedNoteCommand { get; }
    public IAsyncRelayCommand ShowLocalNoteCommand { get; }

    public ObservableCollection<LocalLoveNote> LocalNotes { get; } = [];
    public ObservableCollection<RemoteEnvelope> PendingRemoteNotes { get; } = [];

    public LocalLoveNote? SelectedNote { get => _selectedNote; set => SetProperty(ref _selectedNote, value); }
    public RemoteEnvelope? OpenedRemoteEnvelope { get => _openedRemoteEnvelope; private set => SetProperty(ref _openedRemoteEnvelope, value); }
    public string? OpenedRemoteNoteText { get => _openedRemoteNoteText; private set => SetProperty(ref _openedRemoteNoteText, value); }
    public string DraftText { get => _draftText; set => SetProperty(ref _draftText, value); }
    public bool DraftEnabled { get => _draftEnabled; set => SetProperty(ref _draftEnabled, value); }
    public int DailyLocalNoteLimit => _context.InitialPreferences.LocalNoteDailyLimit;
    public int UnopenedRemoteNoteCount => PendingRemoteNotes.Count;
    public bool HasOpenedRemoteNote => !string.IsNullOrWhiteSpace(OpenedRemoteNoteText);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            LocalNotes.Clear();
            foreach (var note in await _context.LocalNotes.ListAsync(cancellationToken)) LocalNotes.Add(note);
            PendingRemoteNotes.Clear();
            foreach (var envelope in await _context.RemoteEnvelopes.ListPendingAsync(cancellationToken)) PendingRemoteNotes.Add(envelope);
            OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
        });
    }

    public Task SaveLocalNoteAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var text = DraftText.Trim();
            if (text.Length == 0) throw new ArgumentException("Add a note before saving.", nameof(DraftText));
            var note = SelectedNote is null
                ? new LocalLoveNote(Guid.NewGuid().ToString("N"), text, DraftEnabled)
                : SelectedNote with { Text = text, Enabled = DraftEnabled };
            await _context.LocalNotes.SaveToJarAsync(note, cancellationToken);
            Replace(note);
            SelectedNote = note;
        }, "Saved to the local note jar.");

    public Task DeleteLocalNoteAsync(LocalLoveNote? note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        return RunAsync(async () =>
        {
            await _context.LocalNotes.DeleteAsync(note.Id, cancellationToken);
            LocalNotes.Remove(note);
            if (SelectedNote?.Id == note.Id) SelectedNote = null;
        }, "Note deleted from this PC.");
    }

    public Task RevealRemoteNoteAsync(RemoteEnvelope? envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return RunAsync(async () =>
        {
            var text = await _context.RevealRemoteNoteAsync(envelope, cancellationToken);
            OpenedRemoteEnvelope = envelope;
            OpenedRemoteNoteText = text;
            OnPropertyChanged(nameof(HasOpenedRemoteNote));
        });
    }

    public Task SaveOpenedNoteAsync(object? _, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var envelope = OpenedRemoteEnvelope ?? throw new InvalidOperationException("Open a note before saving it.");
            var text = OpenedRemoteNoteText ?? throw new InvalidOperationException("Open a note before saving it.");
            var note = new LocalLoveNote($"remote-{envelope.MessageId}", text);
            await _context.LocalNotes.SaveToJarAsync(note, cancellationToken);
            Replace(note);
            await _context.PresentPetAsync(new PetEvent.Dismissed(envelope.MessageId), cancellationToken);
            PendingRemoteNotes.Remove(envelope);
            OpenedRemoteEnvelope = null;
            OpenedRemoteNoteText = null;
            OnPropertyChanged(nameof(HasOpenedRemoteNote));
            OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
        }, "Saved to the local note jar.");

    public Task ShowLocalNoteAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var note = await _context.NoteSelector.SelectAsync(true, cancellationToken)
                ?? throw new InvalidOperationException("Add a local note before asking Dudu to choose one.");
            OpenedRemoteEnvelope = null;
            OpenedRemoteNoteText = note.Text;
            OnPropertyChanged(nameof(HasOpenedRemoteNote));
        });

    private void Replace(LocalLoveNote note)
    {
        var existing = LocalNotes.FirstOrDefault(item => item.Id == note.Id);
        if (existing is not null) LocalNotes[LocalNotes.IndexOf(existing)] = note;
        else LocalNotes.Add(note);
    }
}
