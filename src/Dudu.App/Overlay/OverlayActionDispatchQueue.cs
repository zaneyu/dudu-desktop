namespace Dudu.App.Overlay;

/// <summary>Owns native pointer dispatch so Win32 callbacks stay non-blocking
/// without abandoning tasks. Work runs in click order and every fault is
/// observed through the host diagnostic callback.</summary>
internal sealed class OverlayActionDispatchQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<Exception> _report;
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public OverlayActionDispatchQueue(Action<Exception> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public Task Completion { get { lock (_gate) return _tail; } }

    /// <summary>A click on Dudu's body: pet it, in click order. The handler
    /// is the router's pet action; it runs serialized behind any earlier
    /// click, and a fault is reported, never rethrown into the native
    /// window procedure.</summary>
    public void EnqueuePet(Func<CancellationToken, Task> petAsync)
    {
        ArgumentNullException.ThrowIfNull(petAsync);
        EnqueueCore(petAsync);
    }

    private void EnqueueCore(Func<CancellationToken, Task> dispatch)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var previous = _tail;
            _tail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    await dispatch(_cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Disposing the queue (window teardown) cancels queued
                    // work; that is not a failure.
                }
                catch (Exception exception)
                {
                    _report(exception);
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
        }
    }
}
