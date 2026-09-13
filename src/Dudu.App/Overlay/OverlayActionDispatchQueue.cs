using Dudu.Core.Assets;

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

    public void Enqueue(OverlayActionSurfaceController surface, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_gate)
        {
            if (_disposed) return;
            var previous = _tail;
            _tail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    await surface.HandlePointerAsync(point, _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
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
