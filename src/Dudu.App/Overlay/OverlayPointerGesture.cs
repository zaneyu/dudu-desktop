using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

/// <summary>
/// Pure click-versus-drag state for a press on the pet body, measured
/// entirely in SCREEN coordinates. The pet window follows the cursor while
/// dragging, so window-relative (client) points stay roughly constant and can
/// never tell a slow drag from a click; comparing screen cursor positions
/// against the screen press point can.
/// </summary>
internal sealed class OverlayPointerGesture
{
    /// <summary>Fallback for SM_CXDRAG / SM_CYDRAG (the Windows default).</summary>
    public const int DefaultDragThreshold = 4;

    private PixelPoint _originScreen;
    private PixelRect _startBounds;
    private int _thresholdX = DefaultDragThreshold;
    private int _thresholdY = DefaultDragThreshold;
    private bool _lastReleaseWasPetBodyClick;

    /// <summary>A pet-body press is in progress.</summary>
    public bool IsPressed { get; private set; }

    /// <summary>The cursor left the drag threshold: this press is a drag and
    /// the window now follows the cursor.</summary>
    public bool HasMoved { get; private set; }

    public PixelPoint OriginScreen => _originScreen;

    public PixelRect StartBounds => _startBounds;

    public void Press(PixelPoint screen, PixelRect windowBounds, int thresholdX, int thresholdY)
    {
        IsPressed = true;
        HasMoved = false;
        _originScreen = screen;
        _startBounds = windowBounds;
        _thresholdX = thresholdX > 0 ? thresholdX : DefaultDragThreshold;
        _thresholdY = thresholdY > 0 ? thresholdY : DefaultDragThreshold;
    }

    /// <summary>
    /// Returns where the window should move for the current cursor position,
    /// or null while the press is still inside the drag threshold (so a plain
    /// click never nudges the pet or dirties the saved placement).
    /// </summary>
    public PixelRect? Move(PixelPoint screen)
    {
        if (!IsPressed)
        {
            return null;
        }

        if (!HasMoved && !ExceedsThreshold(screen))
        {
            return null;
        }

        HasMoved = true;
        return OverlayWindowHost.CalculateDraggedBounds(_startBounds, _originScreen, screen);
    }

    /// <summary>Ends the press. Returns true when it was a click: pressed,
    /// never dragged, and (when known) released inside the threshold.</summary>
    public bool Release(PixelPoint? screen)
    {
        var click = IsPressed
            && !HasMoved
            && (screen is not { } point || !ExceedsThreshold(point));
        IsPressed = false;
        HasMoved = false;
        return click;
    }

    public void Cancel()
    {
        IsPressed = false;
        HasMoved = false;
    }

    /// <summary>Re-anchors an in-progress press after the window was resized
    /// or moved underneath it (WM_DPICHANGED), so the next move does not
    /// snap back to the pre-change size/position.</summary>
    public void Rebase(PixelRect windowBounds, PixelPoint screen)
    {
        _startBounds = windowBounds;
        _originScreen = screen;
    }

    /// <summary>Any new button press starts a fresh click sequence.</summary>
    public void NoteButtonDown() => _lastReleaseWasPetBodyClick = false;

    /// <summary>The last release toggled the action bubble from the pet body.</summary>
    public void NotePetBodyClick() => _lastReleaseWasPetBodyClick = true;

    public void ClearPetBodyClick() => _lastReleaseWasPetBodyClick = false;

    /// <summary>
    /// True when a WM_LBUTTONDBLCLK is the second half of a pet-body double
    /// click: its first click already toggled the bubble, so whatever bubble
    /// action is now under the cursor must not be armed. Consumes the state.
    /// </summary>
    public bool TakeDoubleClickFollowsPetBodyClick()
    {
        var result = _lastReleaseWasPetBodyClick;
        _lastReleaseWasPetBodyClick = false;
        return result;
    }

    private bool ExceedsThreshold(PixelPoint screen) =>
        Math.Abs((long)screen.X - _originScreen.X) > _thresholdX
        || Math.Abs((long)screen.Y - _originScreen.Y) > _thresholdY;
}

/// <summary>
/// Detects a window-state application that was superseded by a nested one.
/// SetWindowPos can synchronously deliver WM_DPICHANGED, whose handler
/// applies newer bounds; the outer call must not then overwrite them with
/// its stale rectangle.
/// </summary>
internal sealed class OverlayWindowStateSequencer
{
    private long _generation;
    private readonly Stack<PixelRect> _pending = new();

    /// <summary>Bounds currently being applied by the innermost in-flight
    /// call, if any.</summary>
    public PixelRect? Pending => _pending.Count == 0 ? null : _pending.Peek();

    public long Begin(PixelRect bounds)
    {
        _pending.Push(bounds);
        return ++_generation;
    }

    public bool IsSuperseded(long token) => token != _generation;

    public void End() => _ = _pending.TryPop(out _);
}
