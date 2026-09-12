using Dudu.Core.Reminders;
using Dudu.Infrastructure.Data;

namespace Dudu.App.Hosting;

public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan ReminderTickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly ReminderEngine _reminderEngine;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _reminderTickGate = new(1, 1);
    private readonly CancellationTokenSource _stopSource = new();
    private Task? _schedulerTask;
    private bool _started;
    private bool _stopRequested;
    private bool _databaseDisposed;

    public AppHost(IServiceProvider services)
        : this(services, AppPaths.ForCurrentUser())
    {
    }

    public AppHost(IServiceProvider services, AppPaths paths)
        : this(
            paths,
            services?.GetService<Database>()
                ?? throw new InvalidOperationException("Database is not registered."),
            services.GetService<ReminderEngine>()
                ?? throw new InvalidOperationException("ReminderEngine is not registered."))
    {
    }

    public AppHost(
        AppPaths paths,
        Database database,
        ReminderEngine reminderEngine)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _reminderEngine = reminderEngine ?? throw new ArgumentNullException(nameof(reminderEngine));
    }

    public bool IsStarted => Volatile.Read(ref _started);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopRequested)
            {
                throw new ObjectDisposedException(nameof(AppHost));
            }

            if (_started)
            {
                return;
            }

            CreateDirectories();

            // Database.InitializeAsync owns opening, migration, and seeding. No
            // background service is started until this and the first reminder
            // reconciliation both complete successfully.
            await _database.InitializeAsync(cancellationToken);
            await RunReminderTickAsync(cancellationToken);

            _started = true;
            _schedulerTask = RunReminderSchedulerAsync(_stopSource.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        RunResumeTickAsync(cancellationToken);

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(cancellationToken);

    public Task HandleResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? schedulerTask;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
            _started = false;
            _stopSource.Cancel();
            schedulerTask = _schedulerTask;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (schedulerTask is not null)
        {
            try
            {
                await schedulerTask.WaitAsync(StopTimeout);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected shutdown path.
            }
            catch (TimeoutException)
            {
                // Do not let an uncooperative optional background tick block
                // application shutdown indefinitely.
            }
        }

        await DisposeDatabaseAsync();
        _stopSource.Dispose();
        _reminderTickGate.Dispose();
        _lifecycleGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopRequested)
        {
            await StopAsync();
        }
    }

    private async Task RunResumeTickAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!_started || _stopRequested)
            {
                return;
            }

            await RunReminderTickAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RunReminderSchedulerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReminderTickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RunReminderTickAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // A transient reminder failure must not stop the local
                    // scheduler; the next tick retries from persisted state.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunReminderTickAsync(CancellationToken cancellationToken)
    {
        await _reminderTickGate.WaitAsync(cancellationToken);
        try
        {
            await _reminderEngine.TickAsync(cancellationToken);
        }
        finally
        {
            _reminderTickGate.Release();
        }
    }

    private void CreateDirectories()
    {
        Directory.CreateDirectory(_paths.Root);
        Directory.CreateDirectory(_paths.Backups);
        Directory.CreateDirectory(_paths.Secrets);
        Directory.CreateDirectory(_paths.Logs);
    }

    private async Task DisposeDatabaseAsync()
    {
        if (_databaseDisposed)
        {
            return;
        }

        _databaseDisposed = true;
        await _database.DisposeAsync();
    }
}
