using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Models;

namespace Dudu.App.Overlay;

/// <summary>State, geometry, and dispatch contract for the no-activate overlay.</summary>
public sealed class OverlayActionSurfaceController : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private OverlayCommandRouter? _router;
    private PixelRect _workArea;
    private PixelPoint _petAnchor;
    private ActionBubbleArrangement? _arrangement;
    private ComfortBubbleArrangement? _comfortArrangement;
    private OverlayActionSurfaceKind _kind = OverlayActionSurfaceKind.Closed;
    private string? _errorMessage;
    private bool _disposed;
    private long _version;

    public ActionBubbleArrangement? Arrangement { get { lock (_gate) return _arrangement; } }
    public ComfortBubbleArrangement? ComfortArrangement { get { lock (_gate) return _comfortArrangement; } }
    public OverlayActionSurfaceKind Kind { get { lock (_gate) return _kind; } }
    public bool IsOpen { get { lock (_gate) return _kind != OverlayActionSurfaceKind.Closed; } }
    public IReadOnlyList<PixelRect> HitRegions { get { lock (_gate) return GetHitRegionsLocked(); } }
    public string? ErrorMessage { get { lock (_gate) return _errorMessage; } }
    public event EventHandler? Changed;

    public void Bind(OverlayCommandRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        OverlayCommandRouter? previous;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _router;
            _router = router;
        }
        if (previous is not null) previous.ComfortPanelChanged -= OnComfortPanelChanged;
        router.ComfortPanelChanged += OnComfortPanelChanged;
    }

    public void ToggleFromPetBody(PixelRect workArea, PixelPoint petAnchor)
    {
        bool close;
        lock (_gate) close = _kind != OverlayActionSurfaceKind.Closed;
        if (close) Close(); else Open(workArea, petAnchor);
    }

    public void Open(PixelRect workArea, PixelPoint petAnchor)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _workArea = workArea;
            _petAnchor = petAnchor;
            ArrangePrimaryLocked();
            _version++;
        }
        RaiseChanged();
    }

    /// <summary>Reflows an open surface after a DPI, scale, or monitor-size
    /// change while preserving the pet anchor's normalized location.</summary>
    public void UpdateViewport(PixelRect workArea)
    {
        if (!workArea.IsValid) throw new ArgumentException("The work area must be valid.", nameof(workArea));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (workArea == _workArea) return;
            if (_kind == OverlayActionSurfaceKind.Closed)
            {
                _workArea = workArea;
                return;
            }

            var normalizedX = _workArea.IsValid
                ? (_petAnchor.X - _workArea.X) / (double)_workArea.Width
                : 0.5;
            var normalizedY = _workArea.IsValid
                ? (_petAnchor.Y - _workArea.Y) / (double)_workArea.Height
                : 0.8;
            _workArea = workArea;
            _petAnchor = new PixelPoint(
                workArea.X + (int)Math.Round(Math.Clamp(normalizedX, 0, 1) * workArea.Width),
                workArea.Y + (int)Math.Round(Math.Clamp(normalizedY, 0, 1) * workArea.Height));
            if (_kind == OverlayActionSurfaceKind.Comfort) ArrangeComfortLocked();
            else ArrangePrimaryLocked();
            _version++;
        }
        RaiseChanged();
    }

    public void Close()
    {
        OverlayCommandRouter? router;
        lock (_gate)
        {
            router = _router;
            _arrangement = null;
            _comfortArrangement = null;
            _kind = OverlayActionSurfaceKind.Closed;
            _errorMessage = null;
            _version++;
        }
        router?.CancelBreathing(closePanel: true);
        RaiseChanged();
    }

    /// <summary>Returns the complete visual and automation contract for the
    /// current native surface.  The same labels and destination routes are
    /// exposed by Home, so the overlay never becomes a mouse-only feature.</summary>
    public OverlaySurfaceSnapshot CreateRenderSnapshot() => CreateRenderSnapshot(null);

    public OverlaySurfaceSnapshot CreateRenderSnapshot(PixelSize? renderSize)
    {
        lock (_gate)
        {
            var actions = _kind switch
            {
                OverlayActionSurfaceKind.Primary => _arrangement?.PrimaryActions
                .Select(item => new OverlaySurfaceAction(
                    ActionBubbleLayout.Label(item.Action),
                    ActionBubbleLayout.AutomationId(item.Action),
                    item.HitRegion,
                    OverlayCommandRouter.EquivalentSettingsDestination(item.Action)))
                .ToArray() ?? [],
                OverlayActionSurfaceKind.Comfort => _comfortArrangement?.Actions
                .Select(item => new OverlaySurfaceAction(
                    ActionBubbleLayout.ComfortLabel(item.Action),
                    ActionBubbleLayout.ComfortAutomationId(item.Action),
                    item.HitRegion,
                    OverlayCommandRouter.EquivalentSettingsDestination(item.Action)))
                .ToArray() ?? [],
                _ => [],
            };
            PixelRect? bounds = _arrangement?.Bounds
                ?? _comfortArrangement?.Bounds
                ?? (_workArea.IsValid ? _workArea : null);
            PixelRect? detailRegion = _arrangement?.DetailRegion
                ?? _comfortArrangement?.DetailRegion
                ?? (_workArea.IsValid
                    ? new PixelRect(_workArea.X + 8, _workArea.Y + 8,
                        Math.Max(1, _workArea.Width - 16), Math.Max(1, _workArea.Height - 16))
                    : null);

            if (renderSize is { } size && size.Width > 0 && size.Height > 0 && _workArea.IsValid)
            {
                bounds = bounds is { } value ? ScaleToRender(value, _workArea, size) : null;
                detailRegion = detailRegion is { } detail ? ScaleToRender(detail, _workArea, size) : null;
                actions = actions
                    .Select(action => action with
                    {
                        HitRegion = ScaleToRender(action.HitRegion, _workArea, size),
                    })
                    .ToArray();
            }

            return new OverlaySurfaceSnapshot(
                _kind,
                bounds,
                actions,
                _router?.ComfortPanel ?? ComfortPanelState.Closed,
                _router?.IsReducedMotion ?? false,
                _errorMessage)
            {
                DetailRegion = detailRegion,
                Theme = _router?.Theme ?? AppTheme.System,
                IsHighContrast = OverlaySurfaceRenderer.IsHighContrastEnabled(),
                GeometryVersion = _version,
            };
        }
    }

    public void Dispose()
    {
        OverlayCommandRouter? router;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            router = _router;
            _router = null;
            Changed = null;
        }
        if (router is not null) router.ComfortPanelChanged -= OnComfortPanelChanged;
    }

    public bool Contains(PixelPoint point)
    {
        lock (_gate) return GetHitRegionsLocked().Any(region => region.Contains(point.X, point.Y));
    }

    public async Task<bool> HandlePointerAsync(PixelPoint point, CancellationToken cancellationToken = default)
    {
        await _dispatchGate.WaitAsync(cancellationToken);
        try
        {
            OverlayCommandRouter? router;
            OverlayAction? primary = null;
            ComfortAction? comfort = null;
            bool contains;
            long version;
            lock (_gate)
            {
                ThrowIfDisposed();
                router = _router;
                version = _version;
                contains = GetHitRegionsLocked().Any(region => region.Contains(point.X, point.Y));
                if (_kind == OverlayActionSurfaceKind.Primary)
                {
                    primary = _arrangement?.PrimaryActions
                        .FirstOrDefault(item => item.HitRegion.Contains(point.X, point.Y))?.Action;
                }
                else if (_kind == OverlayActionSurfaceKind.Comfort)
                {
                    comfort = _comfortArrangement?.Actions
                        .FirstOrDefault(item => item.HitRegion.Contains(point.X, point.Y))?.Action;
                }
            }

            if (router is null)
            {
                if (!contains) return false;
                SetError("Dudu's action surface is not ready yet.");
                return true;
            }

            if (primary is { } primaryAction)
            {
                await DispatchPrimaryAsync(router, primaryAction, version, cancellationToken);
                return true;
            }
            if (comfort is { } comfortAction)
            {
                await DispatchComfortAsync(router, comfortAction, version, cancellationToken);
                return true;
            }
            return false;
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async Task DispatchPrimaryAsync(
        OverlayCommandRouter router,
        OverlayAction action,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (action == OverlayAction.ComfortMe)
            {
                await router.ExecuteAsync(action, cancellationToken);
                lock (_gate)
                {
                    if (_version != expectedVersion || _kind != OverlayActionSurfaceKind.Primary) return;
                    ArrangeComfortLocked();
                    _version++;
                }
            }
            else
            {
                await router.ExecuteAsync(action, cancellationToken);
                if (!TryClose(expectedVersion, router)) return;
            }
            lock (_gate)
            {
                if (_version == expectedVersion || action == OverlayAction.ComfortMe)
                {
                    _errorMessage = null;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError(exception.Message, expectedVersion);
            return;
        }
        RaiseChanged();
    }

    private async Task DispatchComfortAsync(
        OverlayCommandRouter router,
        ComfortAction action,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await router.ExecuteComfortAsync(action, cancellationToken);
            lock (_gate)
            {
                if (_version != expectedVersion || _kind != OverlayActionSurfaceKind.Comfort) return;
                _errorMessage = null;
            }
            if ((action is ComfortAction.Close or ComfortAction.ReadALoveNote)
                && !TryClose(expectedVersion, router)) return;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError(exception.Message, expectedVersion);
            return;
        }
        RaiseChanged();
    }

    private void SetError(string message, long? expectedVersion = null)
    {
        lock (_gate)
        {
            if (expectedVersion is { } version && _version != version) return;
            _errorMessage = message;
            if (_kind == OverlayActionSurfaceKind.Closed && _workArea.IsValid)
            {
                _kind = OverlayActionSurfaceKind.Status;
            }
            _version++;
        }

        RaiseChanged();
    }

    private void OnComfortPanelChanged(object? sender, EventArgs args) => RaiseChanged();

    private void ArrangePrimaryLocked()
    {
        try
        {
            _arrangement = ActionBubbleLayout.TryArrange(
                OverlayCommandRouter.PrimaryActions,
                _workArea,
                _petAnchor);
        }
        catch (ArgumentException)
        {
            _arrangement = null;
        }
        _comfortArrangement = null;
        _kind = _arrangement is null ? OverlayActionSurfaceKind.Status : OverlayActionSurfaceKind.Primary;
        _errorMessage = _arrangement is null
            ? "There is not enough room to show Dudu's actions. Open Settings to use them."
            : null;
    }

    private void ArrangeComfortLocked()
    {
        try
        {
            _comfortArrangement = ActionBubbleLayout.ArrangeComfort(_workArea, _petAnchor);
        }
        catch (ArgumentException)
        {
            _comfortArrangement = null;
        }
        _arrangement = null;
        _kind = _comfortArrangement is null ? OverlayActionSurfaceKind.Status : OverlayActionSurfaceKind.Comfort;
        _errorMessage = _comfortArrangement is null
            ? "There is not enough room to show Dudu's comfort actions. Open Settings to use them."
            : null;
    }

    private bool TryClose(long expectedVersion, OverlayCommandRouter router)
    {
        lock (_gate)
        {
            if (_version != expectedVersion) return false;
            _arrangement = null;
            _comfortArrangement = null;
            _kind = OverlayActionSurfaceKind.Closed;
            _errorMessage = null;
            _version++;
        }
        router.CancelBreathing(closePanel: true);
        RaiseChanged();
        return true;
    }

    private IReadOnlyList<PixelRect> GetHitRegionsLocked() => _kind switch
    {
        OverlayActionSurfaceKind.Primary => _arrangement?.PrimaryActions
            .Select(item => item.HitRegion).ToArray() ?? [],
        OverlayActionSurfaceKind.Comfort => _comfortArrangement?.Actions
            .Select(item => item.HitRegion).ToArray() ?? [],
        _ => [],
    };

    private static PixelRect ScaleToRender(PixelRect value, PixelRect viewport, PixelSize renderSize)
    {
        var left = (int)Math.Round((value.X - viewport.X) * renderSize.Width / (double)viewport.Width);
        var top = (int)Math.Round((value.Y - viewport.Y) * renderSize.Height / (double)viewport.Height);
        var right = (int)Math.Round((value.Right - viewport.X) * renderSize.Width / (double)viewport.Width);
        var bottom = (int)Math.Round((value.Bottom - viewport.Y) * renderSize.Height / (double)viewport.Height);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OverlayActionSurfaceController));
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().OfType<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Dudu overlay change listener failed: {0}", exception);
            }
        }
    }
}
