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
    private readonly IAppHostErrorReporter? _errorReporter;

    public RemoteNoteArrivalSink(
        Func<IUnsolicitedPresentationGateway> gateway,
        IAppHostErrorReporter? errorReporter = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _errorReporter = errorReporter;
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
