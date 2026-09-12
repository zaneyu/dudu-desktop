using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

/// <summary>State and hit-test contract for the no-activate overlay action
/// surface. Slice 3 supplies the WinUI rendering; the native host can already
/// route pointer input through this controller.</summary>
public sealed class OverlayActionSurfaceController
{
    private OverlayCommandRouter? _router;
    private ActionBubbleArrangement? _arrangement;

    public ActionBubbleArrangement? Arrangement => _arrangement;
    public bool IsOpen => _arrangement is not null;
    public IReadOnlyList<PixelRect> HitRegions => _arrangement?.PrimaryActions.Select(item => item.HitRegion).ToArray() ?? [];
    public string? ErrorMessage { get; private set; }
    public event EventHandler? Changed;

    public void Bind(OverlayCommandRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _router.ComfortPanelChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Open(PixelRect workArea, PixelPoint petAnchor)
    {
        _arrangement = ActionBubbleLayout.Arrange(OverlayCommandRouter.PrimaryActions, workArea, petAnchor);
        ErrorMessage = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        _arrangement = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Contains(PixelPoint point) => HitRegions.Any(region =>
        point.X >= region.X && point.X < region.Right && point.Y >= region.Y && point.Y < region.Bottom);

    public async Task<bool> HandlePointerAsync(PixelPoint point, CancellationToken cancellationToken = default)
    {
        var hit = _arrangement?.PrimaryActions.FirstOrDefault(item =>
            point.X >= item.HitRegion.X && point.X < item.HitRegion.Right &&
            point.Y >= item.HitRegion.Y && point.Y < item.HitRegion.Bottom);
        if (hit is null) return false;
        if (_router is null)
        {
            ErrorMessage = "Dudu's action surface is not ready yet.";
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        try
        {
            if (hit.Action == OverlayAction.ComfortMe) _router.OpenComfortPanel();
            else await _router.ExecuteAsync(hit.Action, cancellationToken);
            ErrorMessage = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }
}

internal static class OverlayActionSurfaceObserver
{
    public static async Task ObserveAsync(
        OverlayActionSurfaceController surface,
        PixelPoint point,
        Action<Exception> report)
    {
        try { await surface.HandlePointerAsync(point); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { report(exception); }
    }
}
