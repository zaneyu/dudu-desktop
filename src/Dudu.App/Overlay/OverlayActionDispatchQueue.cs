using Dudu.App.Animation;
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
        EnqueueCore(token => surface.HandlePointerAsync(point, token));
    }

    public void Enqueue(OverlayActionSurfaceController surface, OverlaySurfaceAction action)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(action);
        // Dispatch is strictly serial and a breathing exercise occupies it
        // for a full minute, so a click queued behind it only ran once the
        // exercise ended -- "tiny hug" or "take a five-minute break" pressed
        // mid-breath looked dead and then fired up to a minute later. Any
        // comfort choice now interrupts the running exercise first (Close
        // always did); choosing "breathe with me" again restarts it.
        if (action.ComfortAction == ComfortAction.Close
            || (action.ComfortAction is not null && surface.IsBreathing))
        {
            surface.CancelBreathing();
        }

        EnqueueCore(token => surface.HandlePresentedActionAsync(action, token));
    }

    private void EnqueueCore(Func<CancellationToken, Task<bool>> dispatch)
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
                    // Closing the comfort surface intentionally cancels an
                    // in-progress breathing action before its queued Close.
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
