using Dudu.App.Overlay;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayStaticGuardTests
{
    [Fact]
    public void Pet_window_styles_stay_pinned_to_the_intended_flags()
    {
        // WS_EX_TRANSPARENT is deliberately excluded: it makes a window
        // click-through for *all* input, which would defeat the per-pixel
        // WM_NCHITTEST hit testing OverlayWindowHost relies on for
        // drag/click/scroll (see OverlayHitTest and the WM_NCHITTEST case in
        // OverlayWindowHost.WindowProc).
        Assert.Equal(
            WINDOW_EX_STYLE.WS_EX_LAYERED
                | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            OverlayWindowHost.PetWindowExStyle);
        Assert.False(OverlayWindowHost.PetWindowExStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT));

        // CS_DBLCLKS is required for WM_LBUTTONDBLCLK (double-click to open
        // Home) to ever be delivered to the window procedure.
        Assert.True(OverlayWindowHost.PetWindowClassStyle.HasFlag(WNDCLASS_STYLES.CS_DBLCLKS));
    }

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
