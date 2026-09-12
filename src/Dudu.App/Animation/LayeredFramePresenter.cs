using System.Runtime.InteropServices;
using System.Drawing;
using Dudu.App.Overlay;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.Animation;

/// <summary>
/// Presents borrowed premultiplied BGRA frames to a Win32 layered window.
/// All reads from a <see cref="RenderedFrame"/> happen before this method returns.
/// </summary>
public sealed class LayeredFramePresenter : IFramePresenter, IDisposable
{
    private const byte AC_SRC_ALPHA = 1;
    private readonly object _gate = new();
    private HWND _window;
    private bool _disposed;
    private PresentedFrameInfo? _current;
    private byte[]? _hitTestBuffer;

    public LayeredFramePresenter()
    {
    }

    public LayeredFramePresenter(nint hwnd) => Attach(hwnd);

    public PresentedFrameInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Attach(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("A layered window handle is required.", nameof(hwnd));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _window = new HWND(hwnd);
        }
    }

    public ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_window.IsNull)
            {
                throw new InvalidOperationException("The presenter is not attached to a window.");
            }

            PresentCore(frame, cancellationToken);
            var bytes = frame.PremultipliedBgra.Span;
            if (_hitTestBuffer is null || _hitTestBuffer.Length < bytes.Length)
            {
                _hitTestBuffer = new byte[bytes.Length];
            }
            bytes.CopyTo(_hitTestBuffer);
            _current = new PresentedFrameInfo(frame.Width, frame.Height, frame.Stride, frame.Opacity);
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _window = HWND.Null;
            _current = null;
            _hitTestBuffer = null;
        }
    }

    public bool IsInteractiveAt(int windowX, int windowY, IReadOnlyList<PixelRect>? explicitHitRegions = null)
    {
        lock (_gate)
        {
            return _current is { } current && _hitTestBuffer is { } buffer
                && OverlayHitTest.IsInteractive(
                    buffer.AsSpan(0, checked(current.Stride * current.Height)),
                    current.Width,
                    current.Height,
                    current.Stride,
                    1,
                    windowX,
                    windowY,
                    explicitHitRegions);
        }
    }

    private unsafe void PresentCore(RenderedFrame frame, CancellationToken cancellationToken)
    {
        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull)
        {
            ThrowLastWin32Error("GetDC");
        }

        HDC memoryDc = HDC.Null;
        DeleteObjectSafeHandle? bitmap = null;
        HGDIOBJ previousObject = HGDIOBJ.Null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            memoryDc = PInvoke.CreateCompatibleDC(screenDc);
            if (memoryDc.IsNull)
            {
                ThrowLastWin32Error("CreateCompatibleDC");
            }

            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = frame.Width,
                    biHeight = -frame.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = (uint)BI_COMPRESSION.BI_RGB,
                },
            };

            void* dibBits;
            bitmap = PInvoke.CreateDIBSection(
                memoryDc,
                &bitmapInfo,
                DIB_USAGE.DIB_RGB_COLORS,
                out dibBits,
                null,
                0);
            if (bitmap.IsInvalid || dibBits is null)
            {
                ThrowLastWin32Error("CreateDIBSection");
            }

            previousObject = PInvoke.SelectObject(
                memoryDc,
                new HGDIOBJ(bitmap.DangerousGetHandle()));
            if (previousObject.IsNull)
            {
                ThrowLastWin32Error("SelectObject");
            }

            var bytes = frame.PremultipliedBgra;
            using var pinned = bytes.Pin();
            var source = (byte*)pinned.Pointer;
            var destination = (byte*)dibBits;
            var rowBytes = checked(frame.Width * 4);
            for (var row = 0; row < frame.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Buffer.MemoryCopy(
                    source + row * frame.Stride,
                    destination + row * rowBytes,
                    rowBytes,
                    rowBytes);
            }

            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var sourcePoint = new Point(0, 0);
            var destinationPoint = new Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            if (!PInvoke.UpdateLayeredWindow(
                    _window,
                    screenDc,
                    &destinationPoint,
                    &size,
                    memoryDc,
                    &sourcePoint,
                    new COLORREF(0),
                    &blend,
                    UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
            {
                ThrowLastWin32Error("UpdateLayeredWindow");
            }
        }
        finally
        {
            if (!previousObject.IsNull && !memoryDc.IsNull)
            {
                _ = PInvoke.SelectObject(memoryDc, previousObject);
            }

            if (bitmap is not null)
            {
                bitmap.Dispose();
            }

            if (!memoryDc.IsNull)
            {
                _ = PInvoke.DeleteDC(memoryDc);
            }

            _ = PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    private static void ThrowLastWin32Error(string operation) =>
        throw new InvalidOperationException(
            $"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LayeredFramePresenter));
        }
    }
}

public readonly record struct PresentedFrameInfo(int Width, int Height, int Stride, float Opacity);
