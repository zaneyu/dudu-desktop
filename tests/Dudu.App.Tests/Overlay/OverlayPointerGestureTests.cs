using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayPointerGestureTests
{
    private static readonly PixelRect Start = new(100, 200, 384, 384);

    [Fact]
    public void Slow_drag_is_not_a_click_even_though_the_window_follows_the_cursor()
    {
        // The old check compared WINDOW-relative release points with the
        // window-relative press point. The window moves with the cursor, so
        // those stayed equal during any drag and every slow drag opened the
        // action bubble on release. Screen-space tracking cannot be fooled.
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(300, 400), Start, 4, 4);

        for (var step = 1; step <= 60; step++)
        {
            _ = gesture.Move(new PixelPoint(300 + step, 400 + step));
        }

        Assert.True(gesture.HasMoved);
        Assert.False(gesture.Release(new PixelPoint(360, 460)));
    }

    [Fact]
    public void Movement_inside_the_drag_threshold_neither_moves_the_window_nor_cancels_the_click()
    {
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(300, 400), Start, 4, 4);

        Assert.Null(gesture.Move(new PixelPoint(303, 396)));
        Assert.Null(gesture.Move(new PixelPoint(304, 404)));
        Assert.False(gesture.HasMoved);
        Assert.True(gesture.Release(new PixelPoint(302, 401)));
    }

    [Fact]
    public void Leaving_the_threshold_starts_the_drag_from_the_press_point()
    {
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(300, 400), Start, 4, 4);

        var moved = gesture.Move(new PixelPoint(305, 400));

        Assert.Equal(new PixelRect(105, 200, 384, 384), moved);
        // Once dragging, returning inside the threshold keeps following.
        Assert.Equal(new PixelRect(100, 200, 384, 384), gesture.Move(new PixelPoint(300, 400)));
        Assert.False(gesture.Release(new PixelPoint(300, 400)));
    }

    [Fact]
    public void Release_outside_the_threshold_without_a_move_message_is_not_a_click()
    {
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(300, 400), Start, 4, 4);

        Assert.False(gesture.Release(new PixelPoint(340, 400)));
    }

    [Fact]
    public void Invalid_system_threshold_falls_back_to_the_default()
    {
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(0, 0), Start, 0, -1);

        Assert.Null(gesture.Move(new PixelPoint(OverlayPointerGesture.DefaultDragThreshold, 0)));
        Assert.NotNull(gesture.Move(new PixelPoint(OverlayPointerGesture.DefaultDragThreshold + 1, 0)));
    }

    [Fact]
    public void Rebase_after_a_dpi_resize_keeps_following_from_the_new_bounds()
    {
        var gesture = new OverlayPointerGesture();
        gesture.Press(new PixelPoint(300, 400), Start, 4, 4);
        _ = gesture.Move(new PixelPoint(400, 400));

        gesture.Rebase(new PixelRect(150, 100, 576, 576), new PixelPoint(400, 400));

        Assert.Equal(new PixelRect(160, 100, 576, 576), gesture.Move(new PixelPoint(410, 400)));
    }

    [Fact]
    public void Double_click_after_a_pet_body_click_is_a_pet_double_click()
    {
        // Sequence Windows sends for a double-click: DOWN, UP, DBLCLK, UP.
        // The first UP opened the bubble; the DBLCLK must open Home rather
        // than arm the bubble action now under the cursor.
        var gesture = new OverlayPointerGesture();
        gesture.NoteButtonDown();
        gesture.NotePetBodyClick();

        Assert.True(gesture.TakeDoubleClickFollowsPetBodyClick());
        Assert.False(gesture.TakeDoubleClickFollowsPetBodyClick());
    }

    [Fact]
    public void Double_click_on_a_bubble_action_after_a_separate_press_still_arms_the_action()
    {
        var gesture = new OverlayPointerGesture();
        gesture.NoteButtonDown();
        gesture.NotePetBodyClick();
        // A new press (the first click of a double-click on a bubble button).
        gesture.NoteButtonDown();

        Assert.False(gesture.TakeDoubleClickFollowsPetBodyClick());
    }

    [Fact]
    public void Dispatching_a_bubble_action_clears_the_pet_click_state()
    {
        var gesture = new OverlayPointerGesture();
        gesture.NotePetBodyClick();
        gesture.ClearPetBodyClick();

        Assert.False(gesture.TakeDoubleClickFollowsPetBodyClick());
    }

    [Fact]
    public void Superseded_window_state_is_detected_after_a_nested_apply()
    {
        var sequencer = new OverlayWindowStateSequencer();
        var outer = sequencer.Begin(new PixelRect(0, 0, 384, 384));
        Assert.Equal(new PixelRect(0, 0, 384, 384), sequencer.Pending);

        // WM_DPICHANGED delivered from inside the outer SetWindowPos.
        var nested = sequencer.Begin(new PixelRect(0, 0, 576, 576));
        Assert.False(sequencer.IsSuperseded(nested));
        sequencer.End();

        Assert.True(sequencer.IsSuperseded(outer));
        sequencer.End();
        Assert.Null(sequencer.Pending);
    }
}
