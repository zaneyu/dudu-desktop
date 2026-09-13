namespace Dudu.Core.Abstractions;

/// <summary>
/// Notified once a remote envelope has been durably stored, so the presentation layer can show a
/// generic "a note arrived" notification. Never carries note text or reaction.
/// </summary>
public interface IRemoteNoteArrivalSink
{
    Task NotifyAsync(Guid messageId, CancellationToken cancellationToken);
}

/// <summary>A decrypted note and its reaction, safe to hand to the UI layer.</summary>
public sealed record RevealedRemoteNote(string Text, string Reaction);
