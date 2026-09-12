using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayWindowHostTests
{
    [Fact]
    public void Shutdown_message_has_a_dedicated_owner_route()
    {
        var message = OverlayWindowHost.ShutdownMessageId;

        Assert.True(OverlayWindowHost.IsShutdownMessage(message));
        Assert.False(OverlayWindowHost.IsShutdownMessage(message - 1));
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
}
