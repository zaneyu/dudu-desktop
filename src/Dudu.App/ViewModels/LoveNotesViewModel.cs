using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.ViewModels;

public sealed class LoveNotesViewModel : FeatureViewModelBase
{
    private const string RevealedMessage = "otayyy opened, it stays in your opened notes";

    private readonly CompanionFeatureContext _context;
    private LocalLoveNote? _selectedOpenedNote;
    private LocalLoveNote? _pendingDeleteNote;
    private RemoteEnvelope? _selectedRemoteEnvelope;

    public LoveNotesViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        RevealRemoteNoteCommand = new AsyncRelayCommand<RemoteEnvelope>((item, ct) => RevealRemoteNoteAsync(item, ct));
        DeleteOpenedNoteCommand = new AsyncRelayCommand<LocalLoveNote?>((item, ct) => DeleteOpenedNoteAsync(item, ct));
        RequestDeleteOpenedNoteCommand = new RelayCommand<LocalLoveNote>(RequestDeleteOpenedNote);
        CancelDeleteOpenedNoteCommand = new RelayCommand(() => PendingDeleteNote = null);
        OpenedNotes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoOpenedNotes));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<RemoteEnvelope> RevealRemoteNoteCommand { get; }
    public IAsyncRelayCommand<LocalLoveNote?> DeleteOpenedNoteCommand { get; }
    public IRelayCommand<LocalLoveNote> RequestDeleteOpenedNoteCommand { get; }
    public IRelayCommand CancelDeleteOpenedNoteCommand { get; }

    public ObservableCollection<RemoteEnvelope> PendingRemoteNotes { get; } = [];

    /// <summary>Partner notes she already revealed, newest first. Revealing a note
    /// keeps it here (stored on this pc) until she deletes it.</summary>
    public ObservableCollection<LocalLoveNote> OpenedNotes { get; } = [];

    public LocalLoveNote? SelectedOpenedNote
    {
        get => _selectedOpenedNote;
        set
        {
            if (!SetProperty(ref _selectedOpenedNote, value)) return;
            OnPropertyChanged(nameof(SelectedOpenedNoteText));
            // The pending confirmation names a specific note; once the user looks at
            // something else, confirming should not delete the note they left behind.
            if (PendingDeleteNote is not null && PendingDeleteNote.Id != value?.Id)
            {
                PendingDeleteNote = null;
            }
        }
    }

    /// <summary>The opened note being read: the one just revealed, or the one picked
    /// from the opened list.</summary>
    public string? SelectedOpenedNoteText => SelectedOpenedNote?.Text;

    public LocalLoveNote? PendingDeleteNote
    {
        get => _pendingDeleteNote;
        private set
        {
            if (SetProperty(ref _pendingDeleteNote, value))
            {
                OnPropertyChanged(nameof(IsConfirmingDeleteNote));
                OnPropertyChanged(nameof(DeleteNotePrompt));
            }
        }
    }
    public bool IsConfirmingDeleteNote => PendingDeleteNote is not null;
    public string? DeleteNotePrompt => PendingDeleteNote is null
        ? null
        : $"delete \"{TruncateForPrompt(PendingDeleteNote.Text)}\" for good? cannot undo";
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

    /// <summary>Drives the opened list's empty-state copy.</summary>
    public bool HasNoOpenedNotes => OpenedNotes.Count == 0;
    public int UnopenedRemoteNoteCount => PendingRemoteNotes.Count;
    public string UnopenedRemoteNoteCountText => UnopenedRemoteNoteCount == 1
        ? "1 unopened encrypted note"
        : $"{UnopenedRemoteNoteCount} unopened encrypted notes";
    public bool CanRevealRemoteNote => SelectedRemoteEnvelope is not null;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            var opened = await _context.LocalNotes.ListRemoteAsync(ct);
            var envelopes = await _context.RemoteEnvelopes.ListPendingAsync(ct);
            await MutateAsync(() =>
            {
                // Capture before clearing: the lists' TwoWay SelectedItem bindings
                // write null back into the selections as soon as the rows they
                // point at are removed.
                var selectedMessageId = SelectedRemoteEnvelope?.MessageId;
                var selectedOpenedId = SelectedOpenedNote?.Id;
                PendingRemoteNotes.Clear();
                foreach (var envelope in envelopes) PendingRemoteNotes.Add(envelope);
                OpenedNotes.Clear();
                foreach (var note in opened) OpenedNotes.Add(note);
                // RemoteEnvelope carries byte[] fields, so a reloaded row never
                // equals the old instance: re-point the selection at the fresh
                // row (by message id) so the list highlight and the reveal
                // button keep working after a revisit.
                SelectedRemoteEnvelope = selectedMessageId is null
                    ? null
                    : PendingRemoteNotes.FirstOrDefault(item => item.MessageId == selectedMessageId);
                // Same for the opened note being read; one deleted elsewhere
                // (delete my data, another window) simply drops out.
                SelectedOpenedNote = selectedOpenedId is null
                    ? null
                    : OpenedNotes.FirstOrDefault(item => item.Id == selectedOpenedId);
                OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
                OnPropertyChanged(nameof(UnopenedRemoteNoteCountText));
                // The shell caches pages/view models across visits: a stale pending
                // confirmation from a previous visit must not resurface on this one.
                PendingDeleteNote = null;
            }, ct);
        }, cancellationToken);
    }

    /// <summary>Opening a partner note reads it and keeps it: the decrypted note is
    /// saved as <c>remote-&lt;messageId&gt;</c> and its envelope consumed in one unit of
    /// work, any held copy is discarded, the pet's unread indicator is dismissed, and
    /// then the sender's reaction plays. If the save fails nothing changes: the note
    /// stays unopened and can be revealed again.</summary>
    public Task RevealRemoteNoteAsync(RemoteEnvelope? envelope, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var revealed = await _context.RevealRemoteNoteAsync(envelope, cancellationToken);
            var note = new LocalLoveNote($"remote-{envelope.MessageId}", revealed.Text);
            await _context.FeatureTransactions.SaveRemoteNoteAndConsumeEnvelopeAsync(
                note,
                envelope.MessageId,
                _context.Clock.UtcNow.ToUniversalTime(),
                cancellationToken);
            // The save committed: show it right away, so a follow-up below that
            // fails cannot leave the note listed as unopened.
            await MutateAsync(() =>
            {
                // By message id: after a refresh the list holds new instances
                // (byte[] fields make record equality reference-based), and
                // Remove(envelope) silently left the opened note listed.
                var pending = PendingRemoteNotes.FirstOrDefault(item => item.MessageId == envelope.MessageId);
                if (pending is not null) PendingRemoteNotes.Remove(pending);
                if (SelectedRemoteEnvelope?.MessageId == envelope.MessageId) SelectedRemoteEnvelope = null;
                var existing = OpenedNotes.FirstOrDefault(item => item.Id == note.Id);
                if (existing is not null) OpenedNotes[OpenedNotes.IndexOf(existing)] = note;
                else OpenedNotes.Insert(0, note);
                SelectedOpenedNote = note;
                OnPropertyChanged(nameof(UnopenedRemoteNoteCount));
                OnPropertyChanged(nameof(UnopenedRemoteNoteCountText));
            }, cancellationToken);

            // The remote note is now consumed into the opened list: a queued or
            // held copy of the same message id must not still surface later.
            await _context.DiscardHeldRemoteNoteAsync(envelope.MessageId, cancellationToken);

            // Opening a note means it is read: dismiss the pet's unread indicator for this
            // message id on every reveal, independent of the reaction map below. Without this,
            // only "heart" (whose one-shot re-raises RemoteNoteArrived and then dismisses it as
            // part of its own completion) cleared the indicator, while wave/hug/celebrate/none
            // left "A note arrived" showing for a note the user already opened.
            await _context.PresentPetAsync(new PetEvent.Dismissed(envelope.MessageId), cancellationToken);
            await PresentReactionAsync(revealed.Reaction, envelope.MessageId, cancellationToken);
        }, RevealedMessage);
    }

    // Maps a revealed note's reaction to a one-shot pet presentation, per the ruling: wave ->
    // greeting, heart -> note-arrival, hug -> comfort-hug, celebrate -> celebrate, none -> no
    // one-shot. "heart" replays the note-arrival event the envelope already raised on arrival;
    // the one-shot's completion dismisses it by message id, which is a harmless no-op after
    // the explicit dismiss the reveal just sent for the same id.
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

    /// <summary>Deletes an opened note from this pc for good. Its held copy was
    /// already discarded when it was revealed.</summary>
    public Task DeleteOpenedNoteAsync(LocalLoveNote? note, CancellationToken cancellationToken = default)
    {
        return RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(note);
            await _context.LocalNotes.DeleteAsync(note.Id, cancellationToken);
            await MutateAsync(() =>
            {
                // LocalLoveNote is a record: Remove(note) would use value equality across
                // every field, so a stale UI copy would silently fail to remove. Match by Id.
                var existing = OpenedNotes.FirstOrDefault(item => item.Id == note.Id);
                if (existing is not null) OpenedNotes.Remove(existing);
                if (SelectedOpenedNote?.Id == note.Id) SelectedOpenedNote = null;
                if (PendingDeleteNote?.Id == note.Id) PendingDeleteNote = null;
            }, cancellationToken);
        }, "okkk note deleted le");
    }

    private void RequestDeleteOpenedNote(LocalLoveNote? note)
    {
        if (note is null)
        {
            ReportError(SelectOneFirstMessage);
            return;
        }

        ClearMessages();
        PendingDeleteNote = note;
    }

    /// <summary>Note text is free-form and can be long; keep the confirmation prompt
    /// on one readable line instead of dumping the whole note into it.</summary>
    private const int PromptTruncateLength = 40;

    private static string TruncateForPrompt(string text)
    {
        if (text.Length <= PromptTruncateLength)
        {
            return text;
        }

        // A cut exactly between a UTF-16 surrogate pair (an emoji, most
        // commonly) splits the character and shows a garbage glyph in the
        // delete prompt — back the cut up one char when that would happen.
        var cutLength = PromptTruncateLength;
        if (char.IsHighSurrogate(text[cutLength - 1]))
        {
            cutLength--;
        }

        return text[..cutLength].TrimEnd() + "…";
    }
}

/// <summary>Display helpers for the incoming-notes list, bound from the item
/// template with x:Bind function bindings.</summary>
public static class LoveNoteDisplay
{
    /// <summary>Every row used to read the identical "encrypted note received", so
    /// neither sighted nor screen-reader users could tell notes apart. The local
    /// arrival time distinguishes them without exposing any note content.</summary>
    public static string DescribeEnvelope(DateTimeOffset receivedUtc) =>
        $"encrypted note received {receivedUtc.ToLocalTime().ToString("g", global::System.Globalization.CultureInfo.CurrentCulture)}";
}
