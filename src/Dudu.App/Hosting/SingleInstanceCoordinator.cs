using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Collections.Concurrent;

namespace Dudu.App.Hosting;

public enum AppActivation : byte
{
    OpenHome = 1,
}

public interface IActivationTransport : IAsyncDisposable
{
    Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken);
    Task ListenAsync(Func<ReadOnlyMemory<byte>, Task> onPayload, CancellationToken cancellationToken);
    Task SendAsync(byte payload, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    public const string MutexName = @"Local\DuduDesktop.App.v1";
    public const string PipePrefix = "DuduDesktop.Activation.v1.";
    public static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(2);

    private readonly IActivationTransport _transport;
    private readonly Func<AppActivation, Task> _activationHandler;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _acquisitionGate = new(1, 1);
    private readonly TimeSpan _activationThrottle = TimeSpan.FromMilliseconds(250);
    private DateTimeOffset _lastActivationUtc = DateTimeOffset.MinValue;
    private Task? _listenerTask;
    private bool _isPrimary;
    private bool _disposed;

    public SingleInstanceCoordinator(
        IActivationTransport? transport = null,
        Func<AppActivation, Task>? activationHandler = null)
    {
        _transport = transport ?? CreateDefaultTransport();
        _activationHandler = activationHandler ?? (_ => Task.CompletedTask);
    }

    public bool IsPrimary
    {
        get { lock (_gate) return _isPrimary; }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        await _acquisitionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceCoordinator));
                if (_isPrimary) return true;
            }

            if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
            {
                BecomePrimary();
                return true;
            }

            try
            {
                await ActivatePrimaryAsync(AppActivation.OpenHome, cancellationToken);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                // The mutex can outlive a crashed listener for a small window.
                // A fresh acquisition recovers only after the OS says the
                // previous owner is gone; it never starts a second host.
                if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
                {
                    BecomePrimary();
                    return true;
                }
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
                {
                    BecomePrimary();
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _acquisitionGate.Release();
        }
    }

    public Task ActivatePrimaryAsync(
        AppActivation activation = AppActivation.OpenHome,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(activation))
        {
            throw new ArgumentOutOfRangeException(nameof(activation));
        }

        return _transport.SendAsync((byte)activation, ActivationTimeout, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCancellation.Cancel();
        }

        if (_listenerTask is not null)
        {
            try { await _listenerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }

        await _transport.DisposeAsync();
        _disposeCancellation.Dispose();
        _acquisitionGate.Dispose();
    }

    public static string GetPipeName(string? sid = null)
        => GetPipeName(sid, scope: null);

    internal static string GetPipeName(string? sid, string? scope)
    {
        sid ??= WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;
        var identity = string.IsNullOrWhiteSpace(scope) ? sid : $"{sid}|{Path.GetFullPath(scope)}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return PipePrefix + digest;
    }

    internal static string GetMutexName(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return MutexName;
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(scope))));
        return MutexName + "." + digest;
    }

    private static IActivationTransport CreateDefaultTransport()
    {
        var scope = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        return new WindowsActivationTransport(GetMutexName(scope), GetPipeName(null, scope));
    }

    private async Task ListenUntilDisposedAsync()
    {
        while (!_disposeCancellation.IsCancellationRequested)
        {
            try
            {
                await _transport.ListenAsync(HandlePayloadAsync, _disposeCancellation.Token);
                if (!_disposeCancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), _disposeCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!_disposeCancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _disposeCancellation.Token);
                    }
                    catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
    }

    private async Task HandlePayloadAsync(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != 1 || !Enum.IsDefined((AppActivation)payload.Span[0]))
        {
            return;
        }

        lock (_gate)
        {
            // Rate-limit secondary accepts: coalesce bursts into one
            // activation per throttle window instead of replaying each pipe
            // connect as separate UI work.
            var now = DateTimeOffset.UtcNow;
            if (now - _lastActivationUtc < _activationThrottle)
            {
                return;
            }

            _lastActivationUtc = now;
        }

        try
        {
            await _activationHandler((AppActivation)payload.Span[0]);
        }
        catch
        {
            // An activation is untrusted input. A failed handler must not
            // terminate the primary listener or strand future activations.
        }
    }

    private void BecomePrimary()
    {
        lock (_gate)
        {
            _isPrimary = true;
            _listenerTask = ListenUntilDisposedAsync();
        }
    }
}

internal sealed class WindowsActivationTransport : IActivationTransport
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly BlockingCollection<Action> _ownerCommands = new();
    private readonly TaskCompletionSource<bool> _ownerReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ownerStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _mutexOwnerThread;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private int _disposeRequested;

    public WindowsActivationTransport(string mutexName, string pipeName)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
        _mutexOwnerThread = new Thread(MutexOwnerMain)
        {
            IsBackground = true,
            Name = "Dudu single-instance mutex owner",
        };
        _mutexOwnerThread.Start();
    }

    public async Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken)
    {
        return await InvokeOnMutexOwnerAsync(() =>
        {
            try
            {
                _ownsMutex = _mutex!.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }

            return _ownsMutex;
        }, cancellationToken);
    }

    public async Task ListenAsync(
        Func<ReadOnlyMemory<byte>, Task> onPayload,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(cancellationToken);
                var buffer = new byte[2];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }

                await onPayload(buffer.AsMemory(0, count));

                // Rate-limit accepts so a burst of secondaries cannot hot-loop
                // pipe creation; the bootstrap/coordinator coalesce above
                // makes the delay lossless for identical activations.
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A disconnected or crashed secondary must not kill the
                // primary listener. The next accept creates a fresh pipe.
                await DelayAfterListenerFailureAsync(cancellationToken);
            }
            catch
            {
                // Invalid payloads and handler failures are isolated to this
                // client. Recreate the server and continue accepting.
                await DelayAfterListenerFailureAsync(cancellationToken);
            }
        }
    }

    private static async Task DelayAfterListenerFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task SendAsync(byte payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(timeoutSource.Token);
            await client.WriteAsync(new byte[] { payload }, timeoutSource.Token);
            await client.FlushAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The primary Dudu instance did not accept activation in time.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            await _ownerStopped.Task;
            return;
        }

        await _ownerReady.Task;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerCommands.Add(() =>
        {
            completion.TrySetResult(true);
            _ownerCommands.CompleteAdding();
        });
        await completion.Task;
        await _ownerStopped.Task;
    }

    private async Task<T> InvokeOnMutexOwnerAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _ownerReady.Task.WaitAsync(cancellationToken);
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            throw new ObjectDisposedException(nameof(WindowsActivationTransport));
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ownerCommands.Add(() =>
            {
                try { completion.TrySetResult(operation()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(WindowsActivationTransport));
        }

        return await completion.Task.WaitAsync(cancellationToken);
    }

    private void MutexOwnerMain()
    {
        try
        {
            _mutex = new Mutex(false, _mutexName);
            _ownerReady.TrySetResult(true);
            foreach (var command in _ownerCommands.GetConsumingEnumerable())
            {
                command();
            }
        }
        catch (Exception exception)
        {
            _ownerReady.TrySetException(exception);
        }
        finally
        {
            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); }
                catch (ApplicationException) { }
                _ownsMutex = false;
            }

            _mutex?.Dispose();
            _ownerStopped.TrySetResult(true);
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var security = new PipeSecurity();
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(
            sid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            1,
            1,
            security);
    }
}
