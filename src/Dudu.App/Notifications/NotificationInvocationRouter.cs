using System.Diagnostics;
using Dudu.App.Hosting;

namespace Dudu.App.Notifications;

/// <summary>
/// Acts on a toast click while Dudu is running: a note toast click opens the
/// Love Notes page. Malformed or unknown activations -- including a retired
/// reminder toast's Done/Snooze/body click left in Action Center by an older
/// build -- parse to null and are ignored. Never throws.
/// </summary>
public sealed class NotificationInvocationRouter
{
    public const string NavigateOperation = "notification-invoked";

    private readonly Func<string, CancellationToken, Task> _navigateAsync;
    private readonly IAppHostErrorReporter? _errorReporter;

    public NotificationInvocationRouter(
        Func<string, CancellationToken, Task> navigateAsync,
        IAppHostErrorReporter? errorReporter = null)
    {
        _navigateAsync = navigateAsync ?? throw new ArgumentNullException(nameof(navigateAsync));
        _errorReporter = errorReporter;
    }

    public Task HandleAsync(
        IEnumerable<KeyValuePair<string, string>>? arguments,
        CancellationToken cancellationToken = default) =>
        HandleActivationAsync(NotificationActivation.TryParse(arguments), cancellationToken);

    /// <summary>
    /// Acts on an already parsed activation. Also used on a cold start, where
    /// the click launched Dudu and is only acted on once the runtime is
    /// composed.
    /// </summary>
    public async Task HandleActivationAsync(
        NotificationActivation? activation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (activation is null)
            {
                return;
            }

            await _navigateAsync(activation.Destination, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(NavigateOperation, exception);
        }
    }

    private void Report(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
