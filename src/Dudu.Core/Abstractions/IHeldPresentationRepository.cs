using Dudu.Core.Models;

namespace Dudu.Core.Abstractions;

/// <summary>
/// Local storage boundary for <see cref="HeldPresentation"/> rows -- the
/// durable backing for the held queue in <c>PresentationPolicy</c> /
/// <c>PresentationCoordinator</c>, so a reminder, remote-note arrival, or
/// local note suppressed by quiet hours, fullscreen, a locked session, or a
/// pause survives an app quit or crash instead of being lost with the
/// in-memory queue.
/// </summary>
public interface IHeldPresentationRepository
{
    Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the row keyed by <paramref name="item"/>'s Key.</summary>
    Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Sets the Toasted flag on the row keyed by <paramref name="key"/>
    /// without touching any other column (in particular QueuedUtc). A no-op
    /// when no row exists for the key.</summary>
    Task MarkToastedAsync(string key, CancellationToken cancellationToken);
}
