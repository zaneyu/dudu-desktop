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
}
