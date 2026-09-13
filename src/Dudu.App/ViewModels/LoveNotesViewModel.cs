using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class LoveNotesViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private LocalLoveNote? _selectedNote;
    private RemoteEnvelope? _selectedRemoteEnvelope;
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
    public RemoteEnvelope? SelectedRemoteEnvelope
    {
        get => _selectedRemoteEnvelope;
        set
        {
            if (SetProperty(ref _selectedRemoteEnvelope, value))
            {
                OnPropertyChanged(nameof(CanRevealRemoteNote));
            }
        }
    }
    public RemoteEnvelope? OpenedRemoteEnvelope { get => _openedRemoteEnvelope; private set => SetProperty(ref _openedRemoteEnvelope, value); }
    public string? OpenedRemoteNoteText { get => _openedRemoteNoteText; private set => SetProperty(ref _openedRemoteNoteText, value); }
    public string DraftText { get => _draftText; set => SetProperty(ref _draftText, value); }
    public bool DraftEnabled { get => _draftEnabled; set => SetProperty(ref _draftEnabled, value); }
    public int DailyLocalNoteLimit => _context.CurrentPreferences.LocalNoteDailyLimit;
    public string DailyLocalNoteLimitText => $"up to {DailyLocalNoteLimit} local notes a day";
    public int UnopenedRemoteNoteCount => PendingRemoteNotes.Count;
    public string UnopenedRemoteNoteCountText => UnopenedRemoteNoteCount == 1
        ? "1 unopened encrypted note"
        : $"{UnopenedRemoteNoteCount} unopened encrypted notes";
    public bool HasOpenedRemoteNote => !string.IsNullOrWhiteSpace(OpenedRemoteNoteText);
    public bool CanRevealRemoteNote => SelectedRemoteEnvelope is not null;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            LocalNotes.Clear();
            foreach (var note in await _context.LocalNotes.ListAsync(cancellationToken)) LocalNotes.Add(note);
            PendingRemoteNotes.Clear();
            foreach (var envelope in await _context.RemoteEnvelopes.ListPendingAsync(cancellationToken)) PendingRemoteNotes.Add(envelope);
            if (SelectedRemoteEnvelope is not null
                && PendingRemoteNotes.All(item => item.MessageId != SelectedRemoteEnvelope.MessageId))
            {
                SelectedRemoteEnvelope = null;
            }
            OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
            OnPropertyChanged(nameof(UnopenedRemoteNoteCountText));
            OnPropertyChanged(nameof(DailyLocalNoteLimit));
            OnPropertyChanged(nameof(DailyLocalNoteLimitText));
        });
    }

    public Task SaveLocalNoteAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var text = DraftText.Trim();
            if (text.Length == 0) throw new ArgumentException("aiyo add a note first", nameof(DraftText));
            var note = SelectedNote is null
                ? new LocalLoveNote(Guid.NewGuid().ToString("N"), text, DraftEnabled)
                : SelectedNote with { Text = text, Enabled = DraftEnabled };
            await _context.LocalNotes.SaveToJarAsync(note, cancellationToken);
            Replace(note);
            SelectedNote = note;
        }, "oki saved to the note jar");

    public Task DeleteLocalNoteAsync(LocalLoveNote? note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        return RunAsync(async () =>
        {
            await _context.LocalNotes.DeleteAsync(note.Id, cancellationToken);
            LocalNotes.Remove(note);
            if (SelectedNote?.Id == note.Id) SelectedNote = null;
        }, "okkk note deleted le");
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
            var envelope = OpenedRemoteEnvelope ?? throw new InvalidOperationException("oh no open the note first");
            var text = OpenedRemoteNoteText ?? throw new InvalidOperationException("oh no open the note first");
            var note = new LocalLoveNote($"remote-{envelope.MessageId}", text);
            await _context.FeatureTransactions.SaveRemoteNoteAndConsumeEnvelopeAsync(
                note,
                envelope.MessageId,
                _context.Clock.UtcNow.ToUniversalTime(),
                cancellationToken);
            Replace(note);
            PendingRemoteNotes.Remove(envelope);
            if (SelectedRemoteEnvelope?.MessageId == envelope.MessageId) SelectedRemoteEnvelope = null;
            OpenedRemoteEnvelope = null;
            OpenedRemoteNoteText = null;
            OnPropertyChanged(nameof(HasOpenedRemoteNote));
            OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
            OnPropertyChanged(nameof(UnopenedRemoteNoteCountText));
            await _context.PresentPetAsync(new PetEvent.Dismissed(envelope.MessageId), cancellationToken);
        }, "otayyy saved to the note jar");

    public Task ShowLocalNoteAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var note = await _context.NoteSelector.SelectAsync(true, cancellationToken)
                ?? throw new InvalidOperationException("cannot add a local note first");
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
