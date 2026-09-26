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
    public void Dpi_change_keeps_a_window_that_resolve_already_sized_for_the_monitor()
    {
        // ResolveAndMove sized the window for the 144-DPI monitor before
        // SetWindowPos delivered WM_DPICHANGED; applying the suggested RECT
        // (scaled from the old size) again would double-scale the pet.
        var current = new PixelRect(2000, 100, 576, 576);

        var bounds = OverlayWindowHost.ResolveDpiChangedBounds(
            current,
            new PixelRect(2000, 100, 864, 864),
            new PixelSize(576, 576),
            dragCursor: null);

        Assert.Equal(current, bounds);
    }

    [Fact]
    public void Dpi_change_without_drag_centers_the_dpi_correct_size_on_the_suggestion()
    {
        var bounds = OverlayWindowHost.ResolveDpiChangedBounds(
            new PixelRect(0, 0, 384, 384),
            new PixelRect(1000, 1000, 600, 600),
            new PixelSize(576, 576),
            dragCursor: null);

        Assert.Equal(new PixelRect(1012, 1012, 576, 576), bounds);
    }

    [Fact]
    public void Dpi_change_during_drag_resizes_around_the_cursor_instead_of_being_undone()
    {
        var bounds = OverlayWindowHost.ResolveDpiChangedBounds(
            new PixelRect(1700, 100, 384, 384),
            new PixelRect(1700, 100, 576, 576),
            new PixelSize(576, 576),
            dragCursor: new PixelPoint(1892, 292));

        // Cursor was at the window center; it stays at the center.
        Assert.Equal(new PixelRect(1604, 4, 576, 576), bounds);
    }

    [Theory]
    [InlineData(0x002F, true)]  // SPI_SETWORKAREA: taskbar moved/resized/auto-hide
    [InlineData(0x0000, false)]
    [InlineData(0x0014, false)] // SPI_SETDESKWALLPAPER
    public void Only_work_area_setting_changes_re_resolve_placement(int wParam, bool expected)
    {
        Assert.Equal(expected, OverlayWindowHost.IsWorkAreaSettingChange((nuint)wParam));
    }

    [Fact]
    public void Host_wires_work_area_changes_debounced_wheel_saves_and_clamped_drags()
    {
        var source = OverlaySourceFiles.Read("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");

        var settingCase = source.IndexOf("case WmSettingChange:", StringComparison.Ordinal);
        Assert.True(settingCase >= 0, "WM_SETTINGCHANGE must be handled.");
        Assert.True(
            source.IndexOf("ResolveAndMove();", settingCase, StringComparison.Ordinal)
                < source.IndexOf("break;", settingCase, StringComparison.Ordinal),
            "A work-area change must re-resolve placement.");

        var changeScale = Body(source, "private void ChangeScale(short delta)");
        Assert.Contains("MonitorPlacementService.ApplyWheelDelta(", changeScale, StringComparison.Ordinal);
        Assert.Contains("StartPlacementSaveTimer();", changeScale, StringComparison.Ordinal);
        Assert.Contains("_dragging", changeScale, StringComparison.Ordinal);

        var continueDrag = Body(source, "private void ContinueDrag(LPARAM lParam)");
        Assert.Contains("_gesture.Move(", continueDrag, StringComparison.Ordinal);
        Assert.Contains("MonitorPlacementService.ClampToWorkArea(", continueDrag, StringComparison.Ordinal);

        // Click-vs-drag is decided by the screen-space gesture, never by
        // comparing window-relative points (the window moves with the cursor).
        var handleMessage = Body(source, "private void HandleMessage(uint message, WPARAM wParam, LPARAM lParam)");
        Assert.Contains("_gesture.Release(", handleMessage, StringComparison.Ordinal);
        Assert.Contains("_gesture.TakeDoubleClickFollowsPetBodyClick()", handleMessage, StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected '{signature}'.");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    [Theory]
    [InlineData(0x0312u)] // WM_HOTKEY
    [InlineData(0x0111u)] // WM_COMMAND (tray menu item)
    [InlineData(0x8014u)] // tray callback (WM_APP + 20)
    public void Runtime_claimed_messages_reach_the_runtime_handler_exactly_once(uint message)
    {
        // Production wiring: the host's own message handling forwards every
        // message to the companion event source, whose sink is the SAME
        // runtime handler (hotkey || tray). Routing must therefore never run
        // both paths for a message the runtime already claimed.
        var runtimeCalls = 0;
        var hostCalls = 0;
        bool Runtime(uint routed)
        {
            runtimeCalls++;
            return routed == message;
        }

        var claimed = OverlayWindowHost.RouteWindowMessage(
            0,
            message,
            1,
            2,
            (_, routed, _, _) => Runtime(routed)
                ? OverlayWindowHost.RuntimeMessageDispatch.Claimed
                : OverlayWindowHost.RuntimeMessageDispatch.NotClaimed,
            (_, routed, _, _) =>
            {
                hostCalls++;
                // What WindowsCompanionEventSource.HandleWindowMessage does.
                _ = Runtime(routed);
            });

        Assert.True(claimed);
        Assert.Equal(1, runtimeCalls);
        Assert.Equal(0, hostCalls);
    }

    [Fact]
    public void Unclaimed_messages_still_reach_host_handling_once()
    {
        var hostCalls = 0;

        var claimed = OverlayWindowHost.RouteWindowMessage(
            0,
            0x0201u,
            0,
            0,
            static (_, _, _, _) => OverlayWindowHost.RuntimeMessageDispatch.NotClaimed,
            (_, _, _, _) => hostCalls++);

        Assert.False(claimed);
        Assert.Equal(1, hostCalls);
    }

    [Fact]
    public void Faulted_runtime_handler_is_not_redispatched_through_the_event_source()
    {
        var hostCalls = 0;

        var claimed = OverlayWindowHost.RouteWindowMessage(
            0,
            0x0312u,
            0,
            0,
            static (_, _, _, _) => OverlayWindowHost.RuntimeMessageDispatch.Faulted,
            (_, _, _, _) => hostCalls++);

        Assert.False(claimed);
        Assert.Equal(0, hostCalls);
    }

    [Fact]
    public void Window_procedure_routes_through_the_single_dispatch_helper()
    {
        // Source contract guarding the regression: WindowProc used to call
        // host.HandleMessage(...) and THEN host.TryHandleRuntimeMessage(...),
        // running the runtime handler twice per hotkey/tray message.
        var source = OverlaySourceFiles.Read("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");
        var start = source.IndexOf("private static unsafe LRESULT WindowProc(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("return PInvoke.DefWindowProc(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var body = source[start..end];

        Assert.Contains("RouteWindowMessage(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryHandleRuntimeMessage(", body, StringComparison.Ordinal);
    }
}
