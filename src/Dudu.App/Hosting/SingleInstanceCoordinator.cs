using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

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
    private Task? _listenerTask;
    private bool _isPrimary;
    private bool _disposed;

    public SingleInstanceCoordinator(
        IActivationTransport? transport = null,
        Func<AppActivation, Task>? activationHandler = null)
    {
        _transport = transport ?? new WindowsActivationTransport(MutexName, GetPipeName());
        _activationHandler = activationHandler ?? (_ => Task.CompletedTask);
    }

    public bool IsPrimary
    {
        get { lock (_gate) return _isPrimary; }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceCoordinator));
            if (_isPrimary) return true;
        }

        if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
        {
            lock (_gate) _isPrimary = true;
            _listenerTask = ListenUntilDisposedAsync();
            return true;
        }

        try
        {
            await ActivatePrimaryAsync(AppActivation.OpenHome, cancellationToken);
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            // The mutex can outlive a crashed listener for a small window. A
            // fresh acquisition makes that window recover without starting a
            // second host while the original mutex is owned.
            if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
            {
                lock (_gate) _isPrimary = true;
                _listenerTask = ListenUntilDisposedAsync();
                return true;
            }
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            if (await _transport.TryAcquirePrimaryAsync(cancellationToken))
            {
                lock (_gate) _isPrimary = true;
                _listenerTask = ListenUntilDisposedAsync();
                return true;
            }
        }

        return false;
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
    }

    public static string GetPipeName(string? sid = null)
    {
        sid ??= WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        return PipePrefix + digest;
    }

    private async Task ListenUntilDisposedAsync()
    {
        try
        {
            await _transport.ListenAsync(HandlePayloadAsync, _disposeCancellation.Token);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task HandlePayloadAsync(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != 1 || !Enum.IsDefined((AppActivation)payload.Span[0]))
        {
            return;
        }

        await _activationHandler((AppActivation)payload.Span[0]);
    }
}

internal sealed class WindowsActivationTransport : IActivationTransport
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private bool _ownsMutex;

    public WindowsActivationTransport(string mutexName, string pipeName)
    {
        _mutex = new Mutex(false, mutexName);
        _pipeName = pipeName;
    }

    public Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return Task.FromResult(_ownsMutex);
    }

    public async Task ListenAsync(
        Func<ReadOnlyMemory<byte>, Task> onPayload,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = CreateServer();
            try
            {
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A disconnected or crashed secondary must not kill the
                // primary listener. The next accept creates a fresh pipe.
            }
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

    public ValueTask DisposeAsync()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsMutex = false;
        }

        _mutex.Dispose();
        return ValueTask.CompletedTask;
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
