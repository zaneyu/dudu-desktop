using System.Collections.Concurrent;

namespace Dudu.App.Overlay;

/// <summary>
/// Completes owner-thread work even when teardown, cancellation, or the post
/// signal wins the race. The queue is deliberately platform-light so those
/// races can be tested without creating a Win32 window.
/// </summary>
public sealed class OwnerActionQueue : IDisposable
{
    public static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<QueuedOwnerAction> _actions = new();
    private readonly object _gate = new();
    private readonly Func<bool> _isOwnerThread;
    private readonly Func<Exception?> _post;
    private readonly Action<QueuedOwnerAction> _execute;
    private bool _closed;

    public OwnerActionQueue(
        Func<bool> isOwnerThread,
        Func<Exception?> post,
        Action<QueuedOwnerAction>? execute = null)
    {
        _isOwnerThread = isOwnerThread ?? throw new ArgumentNullException(nameof(isOwnerThread));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _execute = execute ?? (action => action.Run());
    }

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedOwnerAction(
            () =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            exception => Complete(completion, exception),
            cancellationToken);
        TryPost(queued);
        try
        {
            await completion.Task.WaitAsync(InvokeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            // A 5s owner-thread stall is a dropped frame, not an app hang:
            // surface the timeout with owner-busy context so diagnostics can
            // tell contention apart from a deadlock.
            throw new TimeoutException(
                "Owner action dispatch timed out after 5s (dropped frame; owner thread busy or blocked).",
                exception);
        }
    }

    public async Task<TResult> InvokeAsync<TResult>(
        Func<TResult> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedOwnerAction(
            () =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            exception => Complete(completion, exception),
            cancellationToken);
        TryPost(queued);
        try
        {
            return await completion.Task.WaitAsync(InvokeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "Owner action dispatch timed out after 5s (dropped frame; owner thread busy or blocked).",
                exception);
        }
    }

    public void Post(Action action, Action<Exception>? rejection = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        TryPost(new QueuedOwnerAction(
            action,
            rejection ?? (_ => { }),
            CancellationToken.None));
    }

    public void Drain(Func<bool>? stop = null)
    {
        while (_actions.TryDequeue(out var action))
        {
            try
            {
                _execute(action);
            }
            catch (Exception exception)
            {
                action.Fail(exception);
            }

            if (stop?.Invoke() == true)
            {
                break;
            }
        }
    }

    public void Close(Exception? reason = null)
    {
        reason ??= new ObjectDisposedException(nameof(OwnerActionQueue));
        lock (_gate)
        {
            _closed = true;
        }

        while (_actions.TryDequeue(out var action))
        {
            action.Fail(reason);
        }
    }

    public void Dispose() => Close();

    private bool TryPost(QueuedOwnerAction action)
    {
        if (_isOwnerThread())
        {
            lock (_gate)
            {
                if (_closed)
                {
                    action.Fail(new ObjectDisposedException(nameof(OwnerActionQueue)));
                    return false;
                }
            }

            try
            {
                _execute(action);
            }
            catch (Exception exception)
            {
                action.Fail(exception);
            }
            return true;
        }

        lock (_gate)
        {
            if (_closed)
            {
                action.Fail(new ObjectDisposedException(nameof(OwnerActionQueue)));
                return false;
            }

            _actions.Enqueue(action);
        }

        Exception? postFailure;
        try
        {
            postFailure = _post();
        }
        catch (Exception exception)
        {
            postFailure = exception;
        }

        if (postFailure is null)
        {
            return true;
        }

        action.Fail(postFailure);
        return false;
    }

    private static void Complete<T>(TaskCompletionSource<T> completion, Exception exception)
    {
        if (exception is OperationCanceledException canceled
            && canceled.CancellationToken.CanBeCanceled)
        {
            completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (exception is OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        else
        {
            completion.TrySetException(exception);
        }
    }

    public sealed class QueuedOwnerAction
    {
        private readonly object _gate = new();
        private readonly Action _run;
        private readonly Action<Exception> _reject;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenRegistration? _registration;
        private bool _completed;

        internal QueuedOwnerAction(
            Action run,
            Action<Exception> reject,
            CancellationToken cancellationToken)
        {
            _run = run;
            _reject = reject;
            _cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                SetRegistration(cancellationToken.Register(
                    static state => ((QueuedOwnerAction)state!).Cancel(),
                    this));
            }
        }

        internal void Run()
        {
            if (!TryClaim())
            {
                DisposeRegistration();
                return;
            }

            try
            {
                _run();
            }
            finally
            {
                DisposeRegistration();
            }
        }

        internal void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (!TryClaim())
            {
                DisposeRegistration();
                return;
            }

            try
            {
                _reject(exception);
            }
            finally
            {
                DisposeRegistration();
            }
        }

        private void Cancel()
        {
            if (!TryClaim())
            {
                return;
            }

            // The callback cannot dispose its own registration safely. The
            // owner drain or queue close disposes it after claiming the item.
            _reject(new OperationCanceledException(_cancellationToken));
        }

        private bool TryClaim()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                _completed = true;
                return true;
            }
        }

        private void SetRegistration(CancellationTokenRegistration registration)
        {
            var dispose = false;
            lock (_gate)
            {
                if (_completed)
                {
                    dispose = true;
                }
                else
                {
                    _registration = registration;
                }
            }

            if (dispose)
            {
                registration.Dispose();
            }
        }

        private void DisposeRegistration()
        {
            CancellationTokenRegistration? registration;
            lock (_gate)
            {
                registration = _registration;
                _registration = null;
            }

            registration?.Dispose();
        }
    }
}
