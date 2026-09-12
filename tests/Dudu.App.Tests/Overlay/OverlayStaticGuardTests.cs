using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayStaticGuardTests
{
    [Fact]
    public void Native_methods_allowlist_contains_overlay_contract_and_no_handwritten_imports()
    {
        var nativeMethods = ReadRepositoryFile("src", "Dudu.App", "Overlay", "NativeMethods.txt");
        var required = new[]
        {
            "RegisterClassEx", "CreateWindowEx", "DestroyWindow", "UpdateLayeredWindow",
            "WM_NCHITTEST", "WM_MOUSEACTIVATE", "WM_DPICHANGED", "WM_DISPLAYCHANGE",
            "WS_POPUP", "WS_EX_LAYERED", "WS_EX_TOOLWINDOW", "WS_EX_NOACTIVATE",
            "ULW_ALPHA", "AC_SRC_ALPHA", "HTTRANSPARENT", "HTCLIENT", "MA_NOACTIVATE",
        };

        foreach (var symbol in required)
        {
            Assert.Contains(symbol, nativeMethods, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("DllImport", nativeMethods, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_has_no_finalizer_and_keeps_focus_safe_message_paths()
    {
        var host = ReadRepositoryFile("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");

        Assert.DoesNotContain("~OverlayWindowHost", host, StringComparison.Ordinal);
        Assert.Contains("WS_EX_NOACTIVATE", host, StringComparison.Ordinal);
        Assert.Contains("SWP_NOACTIVATE", host, StringComparison.Ordinal);
        Assert.Contains("SW_SHOWNOACTIVATE", host, StringComparison.Ordinal);
        Assert.Contains("SetCapture", host, StringComparison.Ordinal);
        Assert.Contains("ReleaseCapture", host, StringComparison.Ordinal);
        Assert.Contains("WM_DISPLAYCHANGE", ReadRepositoryFile("src", "Dudu.App", "Overlay", "NativeMethods.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_has_borrowed_buffer_and_gdi_cleanup_guards()
    {
        var presenter = ReadRepositoryFile("src", "Dudu.App", "Animation", "LayeredFramePresenter.cs");

        Assert.Contains("PresentAsync", presenter, StringComparison.Ordinal);
        Assert.Contains("bytes.Pin()", presenter, StringComparison.Ordinal);
        Assert.Contains("UpdateLayeredWindow", presenter, StringComparison.Ordinal);
        Assert.Contains("SelectObject", presenter, StringComparison.Ordinal);
        Assert.Contains("DeleteDC", presenter, StringComparison.Ordinal);
        Assert.Contains("ReleaseDC", presenter, StringComparison.Ordinal);
        Assert.Contains("finally", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImport", presenter, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            relativePath.CopyTo(parts, 1);
            var path = Path.Combine(parts);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
