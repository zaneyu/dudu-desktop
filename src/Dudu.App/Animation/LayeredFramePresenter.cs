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
    private IReadOnlyList<PixelRect> _presentedOverlayHitRegions = [];
    private IReadOnlyList<OverlaySurfaceAction> _presentedOverlayActions = [];

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

        // Snapshot placement under the host-update gate, then do all CPU
        // work outside the lock: the gate is shared with the owner's
        // SetWindowPos drag path, so holding it across scaling + UpdateLayeredWindow
        // stalls moves. Hit-test copies and LINQ region scaling are now
        // lock-free; only the final publish re-enters the gate.
        HWND window;
        LayeredWindowState windowState;
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

            window = _window;
            windowState = _windowState;
        }

        PresentCore(window, frame, windowState, cancellationToken);
        var bytes = frame.PremultipliedBgra.Span;
        var hitCopy = new byte[bytes.Length];
        bytes.CopyTo(hitCopy);
        // Region/action scaling is pure math on small lists: run outside the
        // gate and skip entirely when source and client geometry match.
        var overlayHitRegions = ScaleRegionsToClient(
            frame.OverlayHitRegions,
            frame.Width,
            frame.Height,
            windowState.Bounds.Width,
            windowState.Bounds.Height);
        var overlayActions = ScaleActionsToClient(
            frame.OverlaySurface?.Actions ?? [],
            frame.Width,
            frame.Height,
            windowState.Bounds.Width,
            windowState.Bounds.Height);
        var current = new PresentedFrameInfo(
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.Opacity,
            windowState.Bounds,
            windowState.Scale)
        {
            OverlayGeometryVersion = frame.OverlayGeometryVersion,
        };

        lock (_gate)
        {
            ThrowIfDisposed();
            _hitTestBuffer = hitCopy;
            _presentedOverlayHitRegions = overlayHitRegions;
            _presentedOverlayActions = overlayActions;
            _current = current;
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
            _presentedOverlayHitRegions = [];
            _presentedOverlayActions = [];
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
                    explicitHitRegions ?? _presentedOverlayHitRegions);
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

    public OverlaySurfaceAction? FindPresentedOverlayActionAt(int clientX, int clientY)
    {
        lock (_gate)
        {
            return _presentedOverlayActions.FirstOrDefault(action =>
                action.HitRegion.Contains(clientX, clientY));
        }
    }

    internal object HostUpdateGate => _gate;

    internal static IReadOnlyList<PixelRect> ScaleRegionsToClient(
        IReadOnlyList<PixelRect> regions,
        int sourceWidth,
        int sourceHeight,
        int clientWidth,
        int clientHeight)
    {
        if (regions.Count == 0) return [];
        // Fast path: identical geometry needs no math or allocations beyond
        // the copy. Hit regions are value types, so a shallow copy is safe.
        if (sourceWidth == clientWidth && sourceHeight == clientHeight) return [.. regions];
        return regions.Select(region =>
            ScaleRegionToClient(region, sourceWidth, sourceHeight, clientWidth, clientHeight))
            .ToArray();
    }

    internal static IReadOnlyList<OverlaySurfaceAction> ScaleActionsToClient(
        IReadOnlyList<OverlaySurfaceAction> actions,
        int sourceWidth,
        int sourceHeight,
        int clientWidth,
        int clientHeight)
    {
        if (actions.Count == 0) return [];
        if (sourceWidth == clientWidth && sourceHeight == clientHeight) return [.. actions];
        return actions.Select(action => action with
        {
            HitRegion = ScaleRegionToClient(
                action.HitRegion,
                sourceWidth,
                sourceHeight,
                clientWidth,
                clientHeight),
        }).ToArray();
    }

    private static PixelRect ScaleRegionToClient(
        PixelRect region,
        int sourceWidth,
        int sourceHeight,
        int clientWidth,
        int clientHeight)
    {
        var left = (int)Math.Floor(region.X * clientWidth / (double)sourceWidth);
        var top = (int)Math.Floor(region.Y * clientHeight / (double)sourceHeight);
        var right = (int)Math.Ceiling(region.Right * clientWidth / (double)sourceWidth);
        var bottom = (int)Math.Ceiling(region.Bottom * clientHeight / (double)sourceHeight);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

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
        HWND window,
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
            if (update.SourceWidth == update.DestinationWidth && update.SourceHeight == update.DestinationHeight
                && frame.Stride == destinationRowBytes)
            {
                // Fast path: identical size and stride is a straight copy,
                // no per-pixel math. Check cancellation once per 64 rows so
                // large frames stay preemptible without per-pixel overhead.
                var totalBytes = checked(destinationRowBytes * update.DestinationHeight);
                const int ChunkRows = 64;
                var copied = 0;
                while (copied < totalBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = Math.Min(ChunkRows * destinationRowBytes, totalBytes - copied);
                    Buffer.MemoryCopy(source + copied, destination + copied, totalBytes - copied, chunk);
                    copied += chunk;
                }
            }
            else
            {
                // Stretched path: hoist the per-column division into a
                // precomputed X map (one div per destination column, not per
                // pixel) and check cancellation every row so a cancelled
                // animation aborts promptly instead of finishing the blit.
                var mapX = new int[update.DestinationWidth];
                for (var column = 0; column < update.DestinationWidth; column++)
                {
                    mapX[column] = (int)((long)column * update.SourceWidth / update.DestinationWidth);
                }

                for (var row = 0; row < update.DestinationHeight; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceY = (int)((long)row * update.SourceHeight / update.DestinationHeight);
                    var destinationRow = destination + row * destinationRowBytes;
                    var sourceRow = source + sourceY * frame.Stride;
                    for (var column = 0; column < update.DestinationWidth; column++)
                    {
                        var sourcePixel = sourceRow + mapX[column] * 4;
                        var destinationPixel = destinationRow + column * 4;
                        destinationPixel[0] = sourcePixel[0];
                        destinationPixel[1] = sourcePixel[1];
                        destinationPixel[2] = sourcePixel[2];
                        destinationPixel[3] = sourcePixel[3];
                    }
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
                    window,
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
    double Scale)
{
    public long OverlayGeometryVersion { get; init; }
}

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
