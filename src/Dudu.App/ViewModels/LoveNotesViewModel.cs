using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class LoveNotesViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private LocalLoveNote? _selectedNote;
    private LocalLoveNote? _pendingDeleteNote;
    private RemoteEnvelope? _selectedRemoteEnvelope;
    private RemoteEnvelope? _openedRemoteEnvelope;
    private string? _openedRemoteNoteText;
    private string _draftText = string.Empty;
    private bool _draftEnabled = true;

    public LoveNotesViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        SaveLocalNoteCommand = new AsyncRelayCommand((CancellationToken ct) => SaveLocalNoteAsync(ct));
        NewNoteCommand = new RelayCommand(NewNote);
        DeleteLocalNoteCommand = new AsyncRelayCommand<LocalLoveNote?>((item, ct) => DeleteLocalNoteAsync(item, ct));
        RequestDeleteLocalNoteCommand = new RelayCommand<LocalLoveNote>(note => PendingDeleteNote = note);
        CancelDeleteLocalNoteCommand = new RelayCommand(() => PendingDeleteNote = null);
        RevealRemoteNoteCommand = new AsyncRelayCommand<RemoteEnvelope>((item, ct) => RevealRemoteNoteAsync(item, ct));
        SaveOpenedNoteCommand = new AsyncRelayCommand<object?>((item, ct) => SaveOpenedNoteAsync(item, ct));
        ShowLocalNoteCommand = new AsyncRelayCommand((CancellationToken ct) => ShowLocalNoteAsync(ct));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveLocalNoteCommand { get; }
    public IRelayCommand NewNoteCommand { get; }
    public IAsyncRelayCommand<LocalLoveNote?> DeleteLocalNoteCommand { get; }
    public IRelayCommand<LocalLoveNote> RequestDeleteLocalNoteCommand { get; }
    public IRelayCommand CancelDeleteLocalNoteCommand { get; }
    public IAsyncRelayCommand<RemoteEnvelope> RevealRemoteNoteCommand { get; }
    public IAsyncRelayCommand<object?> SaveOpenedNoteCommand { get; }
    public IAsyncRelayCommand ShowLocalNoteCommand { get; }

    public ObservableCollection<LocalLoveNote> LocalNotes { get; } = [];
    public ObservableCollection<RemoteEnvelope> PendingRemoteNotes { get; } = [];

    public LocalLoveNote? SelectedNote { get => _selectedNote; set => SetProperty(ref _selectedNote, value); }
    public LocalLoveNote? PendingDeleteNote
    {
        get => _pendingDeleteNote;
        private set
        {
            if (SetProperty(ref _pendingDeleteNote, value)) OnPropertyChanged(nameof(IsConfirmingDeleteNote));
        }
    }
    public bool IsConfirmingDeleteNote => PendingDeleteNote is not null;
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
        await RunRefreshAsync(async ct =>
        {
            var notes = await _context.LocalNotes.ListAsync(ct);
            var envelopes = await _context.RemoteEnvelopes.ListPendingAsync(ct);
            await MutateAsync(() =>
            {
                LocalNotes.Clear();
                foreach (var note in notes) LocalNotes.Add(note);
                PendingRemoteNotes.Clear();
                foreach (var envelope in envelopes) PendingRemoteNotes.Add(envelope);
                if (SelectedRemoteEnvelope is not null
                    && PendingRemoteNotes.All(item => item.MessageId != SelectedRemoteEnvelope.MessageId))
                {
                    SelectedRemoteEnvelope = null;
                }
                OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
                OnPropertyChanged(nameof(UnopenedRemoteNoteCountText));
                OnPropertyChanged(nameof(DailyLocalNoteLimit));
                OnPropertyChanged(nameof(DailyLocalNoteLimitText));
            }, ct);
        }, cancellationToken);
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
            await MutateAsync(() => Replace(note), cancellationToken);
            NewNote();
        }, "oki saved to the note jar");

    /// <summary>Clears the editor for a fresh note. Runs after every save so typing
    /// fresh text afterward creates a new note instead of silently overwriting the
    /// one just saved.</summary>
    public void NewNote()
    {
        SelectedNote = null;
        DraftText = string.Empty;
        DraftEnabled = true;
    }

    public Task DeleteLocalNoteAsync(LocalLoveNote? note, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(note);
            await _context.LocalNotes.DeleteAsync(note.Id, cancellationToken);
            await MutateAsync(() =>
            {
                LocalNotes.Remove(note);
                if (SelectedNote?.Id == note.Id) SelectedNote = null;
                if (PendingDeleteNote?.Id == note.Id) PendingDeleteNote = null;
            }, cancellationToken);
        }, "okkk note deleted le");
    }

    public Task RevealRemoteNoteAsync(RemoteEnvelope? envelope, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var revealed = await _context.RevealRemoteNoteAsync(envelope, cancellationToken);
            OpenedRemoteEnvelope = envelope;
            OpenedRemoteNoteText = revealed.Text;
            OnPropertyChanged(nameof(HasOpenedRemoteNote));

            // Opening a note means it is read: dismiss the pet's unread indicator for this
            // message id on every reveal, independent of the reaction map below. Without this,
            // only "heart" (whose one-shot re-raises RemoteNoteArrived and then dismisses it as
            // part of its own completion) cleared the indicator, while wave/hug/celebrate/none
            // left "A note arrived" showing for a note the user already opened.
            await _context.PresentPetAsync(new PetEvent.Dismissed(envelope.MessageId), cancellationToken);
            await PresentReactionAsync(revealed.Reaction, envelope.MessageId, cancellationToken);
        });
    }

    // Maps a revealed note's reaction to a one-shot pet presentation, per the ruling: wave ->
    // greeting, heart -> note-arrival, hug -> comfort-hug, celebrate -> celebrate, none -> no
    // one-shot. "heart" replays the note-arrival event the envelope already raised on arrival;
    // the one-shot's completion dismisses it by message id, which is a harmless no-op if the
    // user's later save/dismiss flow (SaveOpenedNoteAsync) repeats it for the same id.
    private Task PresentReactionAsync(string reaction, string messageId, CancellationToken cancellationToken) =>
        reaction switch
        {
            "wave" => _context.PresentOneShotPetAsync(
                new PetEvent.AmbientRequested("greeting"), "greeting", cancellationToken),
            "heart" => _context.PresentOneShotPetAsync(
                new PetEvent.RemoteNoteArrived(messageId), messageId, cancellationToken),
            "celebrate" => _context.PresentOneShotPetAsync(
                new PetEvent.AmbientRequested("celebrate"), "celebrate", cancellationToken),
            "hug" => _context.PresentOneShotPetAsync(
                new PetEvent.ComfortRequested(), "comfort-hug", cancellationToken),
            _ => Task.CompletedTask,
        };

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
            await MutateAsync(() =>
            {
                Replace(note);
                PendingRemoteNotes.Remove(envelope);
                if (SelectedRemoteEnvelope?.MessageId == envelope.MessageId) SelectedRemoteEnvelope = null;
            }, cancellationToken);
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
