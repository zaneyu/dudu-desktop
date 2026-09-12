namespace Dudu.App.Animation;

public interface IFramePresenter
{
    ValueTask PresentAsync(RenderedFrame frame, CancellationToken cancellationToken);
}
