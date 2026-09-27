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

    /// <summary>A pet-body press is in progress.</summary>
    public bool IsPressed { get; private set; }

    /// <summary>The cursor left the drag threshold: this press is a drag and
    /// the window now follows the cursor.</summary>
    public bool HasMoved { get; private set; }

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

    private bool ExceedsThreshold(PixelPoint screen) =>
        Math.Abs((long)screen.X - _originScreen.X) > _thresholdX
        || Math.Abs((long)screen.Y - _originScreen.Y) > _thresholdY;
}
