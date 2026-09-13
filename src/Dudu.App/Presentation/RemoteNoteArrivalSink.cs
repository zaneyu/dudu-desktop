using System.Diagnostics;
using Dudu.Core.Abstractions;

namespace Dudu.App.Presentation;

/// <summary>
/// Converts a newly-stored remote note's message id into a
/// <see cref="DurableNotification.RemoteNote"/> routed through the one presentation gateway, the
/// same shape <see cref="ReminderDueSink"/> uses. Keeps Dudu.Core and Dudu.Infrastructure
/// independent of WinUI: the gateway is resolved lazily because it is not composed yet at the
/// point this sink is registered with dependency injection.
/// </summary>
public sealed class RemoteNoteArrivalSink : IRemoteNoteArrivalSink
{
    private readonly Func<PresentationCoordinator> _gateway;

    public RemoteNoteArrivalSink(Func<PresentationCoordinator> gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            var item = DurableNotification.RemoteNote(messageId.ToString("D"));
            await _gateway().PublishAsync(item, bypassSuppression: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The envelope is already durably recorded before this sink runs; a presentation
            // failure here must never roll that back or break the poll loop.
            Trace.TraceError("Dudu remote note presentation failed: {0}", exception);
        }
    }
}
