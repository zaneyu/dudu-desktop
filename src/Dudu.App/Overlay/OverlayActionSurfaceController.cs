using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Models;

namespace Dudu.App.Overlay;

/// <summary>State, geometry, and dispatch contract for the no-activate overlay.</summary>
public sealed class OverlayActionSurfaceController : IDisposable
{
    private OverlayCommandRouter? _router;
    private PixelRect _workArea;
    private PixelPoint _petAnchor;

    public ActionBubbleArrangement? Arrangement { get; private set; }
    public ComfortBubbleArrangement? ComfortArrangement { get; private set; }
    public OverlayActionSurfaceKind Kind { get; private set; } = OverlayActionSurfaceKind.Closed;
    public bool IsOpen => Kind != OverlayActionSurfaceKind.Closed;
    public IReadOnlyList<PixelRect> HitRegions => Kind switch
    {
        OverlayActionSurfaceKind.Primary => Arrangement?.PrimaryActions.Select(item => item.HitRegion).ToArray() ?? [],
        OverlayActionSurfaceKind.Comfort => ComfortArrangement?.Actions.Select(item => item.HitRegion).ToArray() ?? [],
        _ => [],
    };
    public string? ErrorMessage { get; private set; }
    public event EventHandler? Changed;

    public void Bind(OverlayCommandRouter router)
    {
        if (_router is not null) _router.ComfortPanelChanged -= OnComfortPanelChanged;
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _router.ComfortPanelChanged += OnComfortPanelChanged;
    }

    public void ToggleFromPetBody(PixelRect workArea, PixelPoint petAnchor)
    {
        if (IsOpen) Close();
        else Open(workArea, petAnchor);
    }

    public void Open(PixelRect workArea, PixelPoint petAnchor)
    {
        _workArea = workArea;
        _petAnchor = petAnchor;
        try
        {
            Arrangement = ActionBubbleLayout.TryArrange(OverlayCommandRouter.PrimaryActions, workArea, petAnchor);
        }
        catch (ArgumentException)
        {
            Arrangement = null;
        }
        ComfortArrangement = null;
        Kind = Arrangement is null ? OverlayActionSurfaceKind.Status : OverlayActionSurfaceKind.Primary;
        ErrorMessage = Arrangement is null
            ? "There is not enough room to show Dudu's actions. Open Settings to use them."
            : null;
        RaiseChanged();
    }

    public void Close()
    {
        _router?.CancelBreathing(closePanel: true);
        Arrangement = null;
        ComfortArrangement = null;
        Kind = OverlayActionSurfaceKind.Closed;
        RaiseChanged();
    }

    /// <summary>Returns the complete visual and automation contract for the
    /// current native surface.  The same labels and destination routes are
    /// exposed by Home, so the overlay never becomes a mouse-only feature.</summary>
    public OverlaySurfaceSnapshot CreateRenderSnapshot()
    {
        var actions = Kind switch
        {
            OverlayActionSurfaceKind.Primary => Arrangement?.PrimaryActions
                .Select(item => new OverlaySurfaceAction(
                    ActionBubbleLayout.Label(item.Action),
                    ActionBubbleLayout.AutomationId(item.Action),
                    item.HitRegion,
                    OverlayCommandRouter.EquivalentSettingsDestination(item.Action)))
                .ToArray() ?? [],
            OverlayActionSurfaceKind.Comfort => ComfortArrangement?.Actions
                .Select(item => new OverlaySurfaceAction(
                    ActionBubbleLayout.ComfortLabel(item.Action),
                    ActionBubbleLayout.ComfortAutomationId(item.Action),
                    item.HitRegion,
                    OverlayCommandRouter.EquivalentSettingsDestination(item.Action)))
                .ToArray() ?? [],
            _ => [],
        };

        return new OverlaySurfaceSnapshot(
            Kind,
            Arrangement?.Bounds ?? ComfortArrangement?.Bounds ?? (_workArea.IsValid ? _workArea : null),
            actions,
            _router?.ComfortPanel ?? ComfortPanelState.Closed,
            _router?.IsReducedMotion ?? false,
            ErrorMessage)
        {
            Theme = _router?.Theme ?? AppTheme.System,
            IsHighContrast = OverlaySurfaceRenderer.IsHighContrastEnabled(),
        };
    }

    public void Dispose()
    {
        if (_router is not null) _router.ComfortPanelChanged -= OnComfortPanelChanged;
        _router = null;
        Changed = null;
    }

    public bool Contains(PixelPoint point) => HitRegions.Any(region => region.Contains(point.X, point.Y));

    public async Task<bool> HandlePointerAsync(PixelPoint point, CancellationToken cancellationToken = default)
    {
        if (_router is null)
        {
            if (!Contains(point)) return false;
            SetError("Dudu's action surface is not ready yet.");
            return true;
        }

        if (Kind == OverlayActionSurfaceKind.Primary)
        {
            var hit = Arrangement?.PrimaryActions.FirstOrDefault(item => item.HitRegion.Contains(point.X, point.Y));
            if (hit is null) return false;
            await DispatchPrimaryAsync(hit.Action, cancellationToken);
            return true;
        }

        if (Kind == OverlayActionSurfaceKind.Comfort)
        {
            var hit = ComfortArrangement?.Actions.FirstOrDefault(item => item.HitRegion.Contains(point.X, point.Y));
            if (hit is null) return false;
            await DispatchComfortAsync(hit.Action, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task DispatchPrimaryAsync(OverlayAction action, CancellationToken cancellationToken)
    {
        try
        {
            if (action == OverlayAction.ComfortMe)
            {
                await _router!.ExecuteAsync(action, cancellationToken);
                ComfortArrangement = ActionBubbleLayout.ArrangeComfort(_workArea, _petAnchor);
                Arrangement = null;
                Kind = ComfortArrangement is null ? OverlayActionSurfaceKind.Closed : OverlayActionSurfaceKind.Comfort;
            }
            else
            {
                await _router!.ExecuteAsync(action, cancellationToken);
                Close();
            }
            ErrorMessage = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError(exception.Message);
            return;
        }
        RaiseChanged();
    }

    private async Task DispatchComfortAsync(ComfortAction action, CancellationToken cancellationToken)
    {
        try
        {
            await _router!.ExecuteComfortAsync(action, cancellationToken);
            ErrorMessage = null;
            if (action is ComfortAction.Close or ComfortAction.ReadALoveNote) Close();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError(exception.Message);
            return;
        }
        RaiseChanged();
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        if (Kind == OverlayActionSurfaceKind.Closed && _workArea.IsValid)
        {
            Kind = OverlayActionSurfaceKind.Status;
        }

        RaiseChanged();
    }

    private void OnComfortPanelChanged(object? sender, EventArgs args) => RaiseChanged();

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

internal static class OverlayActionSurfaceObserver
{
    public static async Task ObserveAsync(OverlayActionSurfaceController surface, PixelPoint point, Action<Exception> report)
    {
        try { await surface.HandlePointerAsync(point); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { report(exception); }
    }
}
