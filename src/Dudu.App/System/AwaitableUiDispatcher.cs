namespace Dudu.App.System;

/// <summary>Small testable adapter over the application DispatcherQueue.</summary>
public sealed class AwaitableUiDispatcher
{
    private readonly Func<bool> _hasThreadAccess;
    private readonly Func<Action, bool> _tryEnqueue;

    public AwaitableUiDispatcher(Func<bool> hasThreadAccess, Func<Action, bool> tryEnqueue)
    {
        _hasThreadAccess = hasThreadAccess ?? throw new ArgumentNullException(nameof(hasThreadAccess));
        _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
    }

    public Task InvokeAsync(Action callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        if (_hasThreadAccess())
        {
            callback();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_tryEnqueue(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    callback();
                    completion.TrySetResult();
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException(
                "The WinUI dispatcher rejected the requested action."));
        }

        return completion.Task;
    }
}
