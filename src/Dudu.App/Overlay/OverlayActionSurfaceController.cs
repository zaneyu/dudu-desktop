using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

/// <summary>State, geometry, and dispatch contract for the no-activate overlay.</summary>
public sealed class OverlayActionSurfaceController
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
        Arrangement = ActionBubbleLayout.TryArrange(OverlayCommandRouter.PrimaryActions, workArea, petAnchor);
        ComfortArrangement = null;
        Kind = Arrangement is null ? OverlayActionSurfaceKind.Closed : OverlayActionSurfaceKind.Primary;
        ErrorMessage = Arrangement is null ? "There is not enough room to show Dudu's actions." : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        _router?.CancelBreathing(closePanel: true);
        Arrangement = null;
        ComfortArrangement = null;
        Kind = OverlayActionSurfaceKind.Closed;
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnComfortPanelChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
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
