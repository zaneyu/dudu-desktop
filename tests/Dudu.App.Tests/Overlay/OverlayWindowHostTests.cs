using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayWindowHostTests
{
    [Fact]
    public async Task Shutdown_message_dispatches_on_owner_thread_and_completes_teardown()
    {
        var teardown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerThreadId = 0;
        var callbackThreadId = 0;
        var dispatchResult = false;
        var router = new OverlayOwnerMessageRouter(
            ownerCommandMessage: OverlayWindowHost.HostCommandMessage,
            shutdownMessage: OverlayWindowHost.ShutdownCommandMessage,
            drainOwnerActions: () => throw new InvalidOperationException("Shutdown must not drain owner actions."),
            shutdown: () =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                teardown.TrySetResult(true);
            });
        var ownerThread = new Thread(() =>
        {
            try
            {
                ownerThreadId = Environment.CurrentManagedThreadId;
                dispatchResult = router.Dispatch(OverlayWindowHost.ShutdownCommandMessage);
            }
            finally
            {
                ownerExited.TrySetResult(true);
            }
        });

        ownerThread.Start();
        await teardown.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await ownerExited.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(dispatchResult);
        Assert.Equal(ownerThreadId, callbackThreadId);
    }

    [Fact]
    public void Capture_confirmation_ignores_previous_capture_return_and_checks_current_capture()
    {
        var setCaptureCalls = 0;
        var releaseCalls = 0;
        const nint hwnd = 42;

        var result = OverlayWindowHost.TryConfirmPointerCapture(
            hwnd,
            _ =>
            {
                setCaptureCalls++;
                return 7;
            },
            () => hwnd,
            () => releaseCalls++);

        Assert.True(result);
        Assert.Equal(1, setCaptureCalls);
        Assert.Equal(0, releaseCalls);
    }

    [Fact]
    public void Capture_confirmation_releases_when_current_capture_is_not_the_overlay()
    {
        var releaseCalls = 0;

        var result = OverlayWindowHost.TryConfirmPointerCapture(
            42,
            _ => 0,
            () => 7,
            () => releaseCalls++);

        Assert.False(result);
        Assert.Equal(1, releaseCalls);
    }

    [Fact]
    public void Drag_bounds_use_screen_cursor_coordinates_after_the_window_moves()
    {
        var start = new PixelRect(100, 200, 384, 384);

        var firstMove = OverlayWindowHost.CalculateDraggedBounds(
            start,
            new PixelPoint(150, 250),
            new PixelPoint(190, 280));
        var secondMove = OverlayWindowHost.CalculateDraggedBounds(
            start,
            new PixelPoint(150, 250),
            new PixelPoint(220, 310));

        Assert.Equal(new PixelRect(140, 230, 384, 384), firstMove);
        Assert.Equal(new PixelRect(170, 260, 384, 384), secondMove);
    }

    [Fact]
    public void Single_up_dismisses_an_open_bubble_only_when_every_gate_passes()
    {
        Assert.True(OverlayWindowHost.ShouldDismissBubbleOnPetUp(
            pointerArmed: true,
            suppressedByFlag: false,
            suppressedByTime: false,
            withinSlop: true,
            interactiveAtRelease: true,
            bubbleOpen: true));
    }

    [Fact]
    public void Single_up_never_opens_a_closed_surface()
    {
        // Dismiss-only: even a perfect click does nothing when closed.
        Assert.False(OverlayWindowHost.ShouldDismissBubbleOnPetUp(
            pointerArmed: true,
            suppressedByFlag: false,
            suppressedByTime: false,
            withinSlop: true,
            interactiveAtRelease: true,
            bubbleOpen: false));
    }

    [Theory]
    // unarmed (drag release / press started off-art), suppressed (paired or
    // third-click follow-up), slop exceeded, release over transparent
    // pixels: all must refuse to dismiss, and none may open.
    [InlineData(false, false, false, true, true, true)]
    [InlineData(true, true, false, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, false, false, false, true, true)]
    [InlineData(true, false, false, true, false, true)]
    public void Single_up_refuses_dismissal_when_any_gate_fails(
        bool armed,
        bool suppressedByFlag,
        bool suppressedByTime,
        bool withinSlop,
        bool interactiveAtRelease,
        bool bubbleOpen)
    {
        Assert.False(OverlayWindowHost.ShouldDismissBubbleOnPetUp(
            armed, suppressedByFlag, suppressedByTime,
            withinSlop, interactiveAtRelease, bubbleOpen));
    }

    [Fact]
    public void Context_menu_shows_only_when_interactive_and_not_dragging()
    {
        Assert.True(OverlayWindowHost.ShouldShowContextMenu(interactive: true, dragging: false));
        Assert.False(OverlayWindowHost.ShouldShowContextMenu(interactive: false, dragging: false));
        Assert.False(OverlayWindowHost.ShouldShowContextMenu(interactive: true, dragging: true));
        Assert.False(OverlayWindowHost.ShouldShowContextMenu(interactive: false, dragging: true));
    }

    [Fact]
    public void Menu_button_follows_the_physical_right_button_in_both_swap_modes()
    {
        const uint lUp = 0x0202;
        const uint rUp = 0x0205;

        Assert.False(OverlayWindowHost.IsMenuButtonUp(lUp, buttonsSwapped: false));
        Assert.True(OverlayWindowHost.IsMenuButtonUp(rUp, buttonsSwapped: false));
        // Swapped (left-handed) mouse: physical-left arrives as R-up and
        // must NOT open the menu; physical-right arrives as L-up and does.
        Assert.True(OverlayWindowHost.IsMenuButtonUp(lUp, buttonsSwapped: true));
        Assert.False(OverlayWindowHost.IsMenuButtonUp(rUp, buttonsSwapped: true));
    }

    [Fact]
    public void Click_slop_never_drops_below_four_pixels_and_scales_with_dpi()
    {
        Assert.Equal(4, OverlayWindowHost.ClickSlopForDpi(0));
        Assert.Equal(4, OverlayWindowHost.ClickSlopForDpi(96));
        Assert.Equal(6, OverlayWindowHost.ClickSlopForDpi(144));
        Assert.Equal(8, OverlayWindowHost.ClickSlopForDpi(192));
    }

    [Fact]
    public void Suppression_window_covers_rapid_follow_up_clicks_only()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(OverlayWindowHost.IsWithinSuppressionWindow(
            now, now + TimeSpan.FromMilliseconds(500)));
        Assert.False(OverlayWindowHost.IsWithinSuppressionWindow(
            now + TimeSpan.FromMilliseconds(501), now + TimeSpan.FromMilliseconds(500)));
        Assert.False(OverlayWindowHost.IsWithinSuppressionWindow(now, default));
    }
}
