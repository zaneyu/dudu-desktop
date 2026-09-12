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
    private LayeredWindowState _windowState;
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

            if (!_windowState.IsValid)
            {
                throw new InvalidOperationException("The presenter has no window placement state.");
            }

            PresentCore(frame, _windowState, cancellationToken);
            var bytes = frame.PremultipliedBgra.Span;
            if (_hitTestBuffer is null || _hitTestBuffer.Length < bytes.Length)
            {
                _hitTestBuffer = new byte[bytes.Length];
            }
            bytes.CopyTo(_hitTestBuffer);
            _current = new PresentedFrameInfo(
                frame.Width,
                frame.Height,
                frame.Stride,
                frame.Opacity,
                _windowState.Bounds,
                _windowState.Scale);
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
                    current.Bounds.Width,
                    current.Bounds.Height,
                    windowX,
                    windowY,
                    explicitHitRegions);
        }
    }

    public void SetWindowState(PixelRect bounds, double scale)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        if (!double.IsFinite(scale)
            || scale is < MonitorPlacementService.MinimumScale or > MonitorPlacementService.MaximumScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _windowState = new LayeredWindowState(bounds, scale);
        }
    }

    internal object HostUpdateGate => _gate;

    /// <summary>
    /// Describes the immutable placement inputs used by UpdateLayeredWindow.
    /// The host owns <see cref="LayeredWindowState.Bounds"/>; presentation may not
    /// choose a destination or resize the host window independently.
    /// </summary>
    public static LayeredFrameUpdate CreateUpdate(
        LayeredWindowState windowState,
        int sourceWidth,
        int sourceHeight)
    {
        if (!windowState.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(windowState));
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        return new LayeredFrameUpdate(
            windowState.Bounds,
            sourceWidth,
            sourceHeight,
            windowState.Bounds.Width,
            windowState.Bounds.Height,
            windowState.Scale);
    }

    private unsafe void PresentCore(
        RenderedFrame frame,
        LayeredWindowState windowState,
        CancellationToken cancellationToken)
    {
        var update = CreateUpdate(windowState, frame.Width, frame.Height);
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
                    biWidth = update.DestinationWidth,
                    biHeight = -update.DestinationHeight,
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
            var destinationRowBytes = checked(update.DestinationWidth * 4);
            for (var row = 0; row < update.DestinationHeight; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceY = (int)((long)row * update.SourceHeight / update.DestinationHeight);
                var destinationRow = destination + row * destinationRowBytes;
                var sourceRow = source + sourceY * frame.Stride;
                for (var column = 0; column < update.DestinationWidth; column++)
                {
                    var sourceX = (int)((long)column * update.SourceWidth / update.DestinationWidth);
                    var sourcePixel = sourceRow + sourceX * 4;
                    var destinationPixel = destinationRow + column * 4;
                    destinationPixel[0] = sourcePixel[0];
                    destinationPixel[1] = sourcePixel[1];
                    destinationPixel[2] = sourcePixel[2];
                    destinationPixel[3] = sourcePixel[3];
                }
            }

            var size = new SIZE { cx = update.DestinationWidth, cy = update.DestinationHeight };
            var sourcePoint = new Point(0, 0);
            var destinationPoint = new Point(update.Bounds.X, update.Bounds.Y);
            var blendConfiguration = CreateBlendConfiguration(frame.Opacity);
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = blendConfiguration.SourceConstantAlpha,
                AlphaFormat = blendConfiguration.AlphaFormat,
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

    public static LayeredBlendConfiguration CreateBlendConfiguration(float opacity)
    {
        if (float.IsNaN(opacity) || float.IsInfinity(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        // RenderedFrame pixels are already premultiplied by SkiaFrameComposer.
        // Applying opacity here would multiply their alpha a second time.
        return new LayeredBlendConfiguration(byte.MaxValue, AC_SRC_ALPHA);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LayeredFramePresenter));
        }
    }
}

public readonly record struct PresentedFrameInfo(
    int Width,
    int Height,
    int Stride,
    float Opacity,
    PixelRect Bounds,
    double Scale);

public readonly record struct LayeredFrameUpdate(
    PixelRect Bounds,
    int SourceWidth,
    int SourceHeight,
    int DestinationWidth,
    int DestinationHeight,
    double Scale);

public readonly record struct LayeredBlendConfiguration(
    byte SourceConstantAlpha,
    byte AlphaFormat);

public readonly record struct LayeredWindowState(PixelRect Bounds, double Scale)
{
    public bool IsValid => Bounds.IsValid
        && double.IsFinite(Scale)
        && Scale is >= MonitorPlacementService.MinimumScale and <= MonitorPlacementService.MaximumScale;
}
