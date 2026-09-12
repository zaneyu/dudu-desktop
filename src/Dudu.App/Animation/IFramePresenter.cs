namespace Dudu.App.Animation;

/// <summary>
/// Consumes a rendered frame synchronously with respect to its borrowed pixel buffer.
/// Implementations must finish every read of <see cref="RenderedFrame.Bytes"/> and every
/// native pointer obtained from it before the returned operation completes. The engine
/// returns the pooled buffer immediately after this operation completes; presenters that
/// need deferred work must copy the bytes or retain their own native buffer first.
/// </summary>
public interface IFramePresenter
{
    /// <summary>
    /// Presents <paramref name="frame"/> while its borrowed buffer is valid.
    /// </summary>
    ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken);
}
