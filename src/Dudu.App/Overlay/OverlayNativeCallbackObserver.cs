namespace Dudu.App.Overlay;

internal static class OverlayNativeCallbackObserver
{
    public static async Task ObserveAsync(Task callback, Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            await callback;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            report(exception);
        }
    }
}
