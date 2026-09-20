using System.Threading.Channels;
using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class AppHostTests
{
    [Fact]
    public async Task Startup_tick_advances_the_reminder_engine_but_does_not_release_held_presentations()
    {
        // Finding 2: RunStartAsync used to run the presentation gateway's
        // release tick (TickAsync) as part of starting up, before
        // WindowsCompanionBootstrap has pushed real fullscreen/session-lock
        // state (via the events sink) or run the startup visibility gate --
        // so a held item could be released, animated into a window that
        // might not even be shown yet, and its row deleted on "success", all
        // before the app's actual visibility state was known. The reminder
        // engine itself must still tick at startup; only the presentation
        // release is deferred to the first regularly scheduled 30 s tick.
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"dudu-apphost-{Guid.NewGuid():N}");
        try
        {
            var reminderService = new RecordingReminderService();
            var timerFactory = new ManualTimerFactory();
            var gateway = new RecordingPresentationGateway();
            var host = new AppHost(
                AppPaths.ForRoot(root),
                new NoOpDatabase(),
                reminderService,
                timerFactory);
            host.AttachPresentationGateway(gateway);

            await host.StartAsync(cancellationToken);

            Assert.Equal(1, reminderService.TickCalls);
            Assert.Equal(1, gateway.StartCalls);
            Assert.Equal(0, gateway.TickCalls);

            // The first regularly scheduled tick -- after WindowsCompanion
            // Bootstrap has had a chance to push real state -- does release.
            timerFactory.Timer!.SignalTick();
            await gateway.TickInvoked.Task.WaitAsync(cancellationToken);

            Assert.Equal(2, reminderService.TickCalls);
            Assert.Equal(1, gateway.TickCalls);

            await host.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoOpDatabase : IAppHostDatabase
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingReminderService : IAppHostReminderService
    {
        public int TickCalls { get; private set; }

        public Task TickAsync(CancellationToken cancellationToken = default)
        {
            TickCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPresentationGateway : IAppHostPresentationGateway
    {
        public TaskCompletionSource TickInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCalls { get; private set; }

        public int TickCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task TickAsync(CancellationToken cancellationToken = default)
        {
            TickCalls++;
            TickInvoked.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A timer whose ticks are fired on demand from the test rather
    /// than on a wall-clock interval, so the "first scheduled tick" can be
    /// deterministically triggered after asserting on startup's own tick.</summary>
    private sealed class ManualTimer : IAppHostTimer
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();

        public void SignalTick() => _ticks.Writer.TryWrite(true);

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default) =>
            await _ticks.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimerFactory : IAppHostTimerFactory
    {
        public ManualTimer? Timer { get; private set; }

        public IAppHostTimer Create(TimeSpan interval) => Timer = new ManualTimer();
    }
}
