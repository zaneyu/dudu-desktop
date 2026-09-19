using System.Diagnostics;
using Dudu.App.Hosting;
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
    private readonly Func<IUnsolicitedPresentationGateway> _gateway;
    private IAppHostErrorReporter? _errorReporter;

    public RemoteNoteArrivalSink(
        Func<IUnsolicitedPresentationGateway> gateway,
        IAppHostErrorReporter? errorReporter = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _errorReporter = errorReporter;
    }

    /// <summary>
    /// Settable so the production composition can attach the shared AppHost
    /// sink after construction: the sink is registered before the host — and
    /// therefore its error reporter — exists. Mirrors <see cref="ReminderDueSink.ErrorReporter"/>.
    /// </summary>
    public IAppHostErrorReporter? ErrorReporter
    {
        get => _errorReporter;
        set => _errorReporter = value;
    }

    public async Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
    {
        // Resolving the gateway is deliberately outside the try/catch below, but propagating
        // that failure does NOT get the popup retried: RemoteSyncService stores the envelope
        // locally and marks it processed before calling this sink, so the envelope is already
        // committed as processed by the time a not-ready gateway (safe mode, or a startup race
        // before the overlay finishes composing) throws here. The next poll takes the
        // already-processed shortcut and acks the relay copy without ever calling this sink
        // again -- the note itself is never lost (it is already revealable through Love Notes,
        // CompanionFeatureContext.RemoteEnvelopes), but its popup is not retried. (Tracked
        // separately; not fixed by this comment change.)
        var gateway = _gateway();
        try
        {
            var item = DurableNotification.RemoteNote(messageId.ToString("D"));
            await gateway.PublishAsync(item, bypassSuppression: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A genuine presentation failure (the gateway exists but PublishAsync itself threw)
            // is swallowed: the note is already durably stored and revealable as above, and a
            // failure here must never roll that back or break the poll loop. It now leaves
            // an operation-named diagnostic carrying the exception type only — the
            // message id itself is never logged.
            ReportFailure("remote-note-notify", exception);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
