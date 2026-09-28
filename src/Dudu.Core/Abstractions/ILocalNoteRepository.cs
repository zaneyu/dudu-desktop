using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

public interface ILocalNoteRepository
{
    /// <summary>Lists every row in <c>local_notes</c>, including dormant legacy rows (seeded
    /// defaults, older hand-written jar notes) that nothing presents any more.</summary>
    Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<LocalLoveNote>>(new NotSupportedException(
            "This local-note repository does not support jar management."));

    /// <summary>Lists the partner notes she revealed (ids starting with <c>remote-</c>),
    /// most recently first saved first. Other rows (seeded defaults, older hand-written
    /// jar notes) are never listed.</summary>
    Task<IReadOnlyList<LocalLoveNote>> ListRemoteAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<LocalLoveNote>>(new NotSupportedException(
            "This local-note repository does not list opened partner notes."));

    /// <summary>Explicitly saves a note to the local-only love-note jar.</summary>
    Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This local-note repository does not support jar management."));

    Task DeleteAsync(string noteId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This local-note repository does not support jar management."));
}
