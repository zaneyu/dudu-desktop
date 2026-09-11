using Dudu.Core.Abstractions;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Focus;

public sealed class FocusServiceTests
{
    [Fact]
    public async Task Reload_uses_persisted_end_time_instead_of_tick_count()
    {
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository();
        var first = new FocusService(repository, clock);
        var started = await first.StartAsync(null, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMinutes(10));
        var reloaded = new FocusService(repository, clock);
        var snapshot = await reloaded.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(started.Id, snapshot!.Id);
        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.Remaining);
    }

    [Fact]
    public async Task Paused_time_does_not_consume_remaining_duration()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        await fixture.Service.PauseAsync(fixture.SessionId, fixture.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var result = await fixture.Service.ResumeAsync(fixture.SessionId, fixture.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(25), result.Remaining);
    }

    [Fact]
    public async Task Second_active_session_is_rejected()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.StartAsync(null, TimeSpan.FromMinutes(10), fixture.CancellationToken));

        Assert.Contains("another focus session is active", exception.Message);
    }

    [Fact]
    public async Task Concurrent_starts_allow_exactly_one_session()
    {
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository
        {
            CoordinateAtomicCreates = true,
        };
        var first = new FocusService(repository, clock);
        var second = new FocusService(repository, clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(
            Task.Run(() => CaptureAsync(() => first.StartAsync(
                    null,
                    TimeSpan.FromMinutes(25),
                    cancellationToken))),
            Task.Run(() => CaptureAsync(() => second.StartAsync(
                    null,
                    TimeSpan.FromMinutes(25),
                    cancellationToken))));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        Assert.Equal(1, results.Count(result =>
            result.Exception is InvalidOperationException exception &&
            exception.Message.Contains("another focus session is active", StringComparison.Ordinal)));
        Assert.Single(repository.Sessions);
    }

    [Fact]
    public async Task Extension_moves_end_time_by_requested_duration()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await fixture.Service.ExtendAsync(
            fixture.SessionId,
            TimeSpan.FromMinutes(10),
            fixture.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(30), result.Remaining);
    }

    [Fact]
    public async Task Early_end_records_ended_early()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));

        var result = await fixture.Service.EndAsync(fixture.SessionId, fixture.CancellationToken);

        Assert.Equal(FocusStatus.EndedEarly, result.Status);
        Assert.Equal(TimeSpan.Zero, result.Remaining);
    }

    [Fact]
    public async Task Expired_running_session_completes_once()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        fixture.Clock.Advance(TimeSpan.FromMinutes(25));

        Assert.True(await fixture.Service.CompleteExpiredAsync(fixture.SessionId, fixture.CancellationToken));
        Assert.False(await fixture.Service.CompleteExpiredAsync(fixture.SessionId, fixture.CancellationToken));
        Assert.Equal(FocusStatus.Completed, fixture.Repository.Sessions[fixture.SessionId].Status);
    }

    [Fact]
    public async Task Concurrent_expiry_completions_return_true_exactly_once()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        fixture.Repository.CoordinateReads = true;
        fixture.Clock.Advance(TimeSpan.FromMinutes(25));
        var cancellationToken = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(
            Task.Run(() => fixture.Service.CompleteExpiredAsync(
                fixture.SessionId,
                cancellationToken)),
            Task.Run(() => fixture.Service.CompleteExpiredAsync(
                fixture.SessionId,
                cancellationToken)));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(2, fixture.Repository.CompareAndSetAttempts);
        Assert.Equal(FocusStatus.Completed, fixture.Repository.Sessions[fixture.SessionId].Status);
    }

    [Fact]
    public async Task Session_preserves_optional_task_id()
    {
        var taskId = Guid.Parse("71ad44e7-0ab3-43e4-982e-93f8c8a1d46a");
        var tasks = new InMemoryTaskRepository();
        tasks.Tasks[taskId] = new TaskItem(
            taskId,
            "Task",
            null,
            null,
            false,
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"),
            null);
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository();
        var service = new FocusService(repository, tasks, clock);

        var result = await service.StartAsync(taskId, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken);

        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(taskId, repository.Sessions[result.Id].TaskId);
    }

    [Fact]
    public async Task Completed_task_cannot_start_focus()
    {
        var taskId = Guid.NewGuid();
        var tasks = new InMemoryTaskRepository();
        tasks.Tasks[taskId] = new TaskItem(
            taskId,
            "Done",
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var service = new FocusService(new InMemoryFocusRepository(), tasks, new FakeClock("2026-09-11T10:00:00Z"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(taskId, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken));
    }

    private sealed class FocusFixture
    {
        private FocusFixture(
            FocusService service,
            InMemoryFocusRepository repository,
            FakeClock clock,
            Guid sessionId)
        {
            Service = service;
            Repository = repository;
            Clock = clock;
            SessionId = sessionId;
        }

        public FocusService Service { get; }
        public InMemoryFocusRepository Repository { get; }
        public FakeClock Clock { get; }
        public Guid SessionId { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public static FocusFixture Started(TimeSpan duration)
        {
            var clock = new FakeClock("2026-09-11T10:00:00Z");
            var repository = new InMemoryFocusRepository();
            var service = new FocusService(repository, clock);
            var session = service.StartAsync(null, duration, CancellationToken.None).GetAwaiter().GetResult();
            return new FocusFixture(service, repository, clock, session.Id);
        }
    }

    private sealed class FakeClock(string initialUtc) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse(initialUtc);

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class InMemoryFocusRepository : IFocusSessionRepository
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _readGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _createGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<Guid, FocusSession> Sessions { get; } = [];

        public bool CoordinateAtomicCreates { get; init; }

        public bool CoordinateReads { get; set; }

        public int CompareAndSetAttempts { get; private set; }

        private int _readers;
        private int _creators;

        public async Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            FocusSession? session;
            var waitForReaders = false;
            lock (_gate)
            {
                session = Sessions.GetValueOrDefault(id);
                if (CoordinateReads && _readers++ < 2)
                {
                    waitForReaders = true;
                    if (_readers == 2)
                    {
                        _readGate.TrySetResult(true);
                    }
                }
            }

            if (waitForReaders)
            {
                await _readGate.Task;
            }

            return session;
        }

        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Sessions.Values.FirstOrDefault(session =>
                    session.Status is FocusStatus.Running or FocusStatus.Paused));
            }
        }

        public async Task<bool> TryCreateActiveAsync(
            FocusSession session,
            CancellationToken cancellationToken)
        {
            var waitForCreators = false;
            lock (_gate)
            {
                if (CoordinateAtomicCreates && _creators++ < 2)
                {
                    waitForCreators = true;
                    if (_creators == 2)
                    {
                        _createGate.TrySetResult(true);
                    }
                }
            }

            if (waitForCreators)
            {
                await _createGate.Task;
            }

            lock (_gate)
            {
                if (Sessions.Values.Any(existing =>
                    existing.Status is FocusStatus.Running or FocusStatus.Paused))
                {
                    return false;
                }

                Sessions[session.Id] = session;
                return true;
            }
        }

        public Task<bool> TryCompareAndSetAsync(
            FocusSession expected,
            FocusSession replacement,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CompareAndSetAttempts++;
                if (!Sessions.TryGetValue(expected.Id, out var current) || current != expected)
                {
                    return Task.FromResult(false);
                }

                Sessions[replacement.Id] = replacement;
                return Task.FromResult(true);
            }
        }

        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Sessions[session.Id] = session;
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<StartResult> CaptureAsync(Func<Task<FocusSnapshot>> operation)
    {
        try
        {
            return new StartResult(await operation(), null);
        }
        catch (Exception exception)
        {
            return new StartResult(null, exception);
        }
    }

    private sealed record StartResult(FocusSnapshot? Snapshot, Exception? Exception)
    {
        public bool Succeeded => Snapshot is not null;
    }

    private sealed class InMemoryTaskRepository : ITaskRepository
    {
        public Dictionary<Guid, TaskItem> Tasks { get; } = [];

        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Tasks.GetValueOrDefault(id));

        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskItem>>(Tasks.Values.Where(task => !task.IsCompleted).ToArray());

        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }
    }
}
