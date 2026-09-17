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
    public async Task Extension_after_an_overnight_pause_uses_focus_time_not_wall_clock_time()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        await fixture.Service.PauseAsync(fixture.SessionId, fixture.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(24));
        await fixture.Service.ResumeAsync(fixture.SessionId, fixture.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await fixture.Service.ExtendAsync(
            fixture.SessionId,
            TimeSpan.FromMinutes(5),
            fixture.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(25), result.Remaining);
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
        var service = new FocusService(repository, clock, tasks);

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
        var service = new FocusService(new InMemoryFocusRepository(), new FakeClock("2026-09-11T10:00:00Z"), tasks);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(taskId, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Single_constructor_accepts_an_optional_task_repository()
    {
        var repository = new InMemoryFocusRepository();
        var clock = new FakeClock("2026-09-11T10:00:00Z");

        var withoutTasks = new FocusService(repository, clock);
        var started = await withoutTasks.StartAsync(null, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(25), started.Remaining);

        var withTasks = new FocusService(repository, clock, taskRepository: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            withTasks.StartAsync(Guid.NewGuid(), TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Has_exactly_one_public_constructor_so_container_resolution_is_unambiguous()
    {
        var constructor = Assert.Single(typeof(FocusService).GetConstructors());
        var optional = constructor.GetParameters().Where(parameter => parameter.IsOptional).ToArray();
        Assert.Equal(typeof(ITaskRepository), Assert.Single(optional).ParameterType);
    }

    [Fact]
    public async Task Start_rejects_durations_longer_than_24_hours()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        await fixture.Service.EndAsync(fixture.SessionId, fixture.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.StartAsync(null, TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)), fixture.CancellationToken));
        var ok = await fixture.Service.StartAsync(null, TimeSpan.FromHours(24), fixture.CancellationToken);
        Assert.Equal(TimeSpan.FromHours(24), ok.Remaining);
    }

    [Fact]
    public async Task Extend_rejects_extensions_that_push_a_session_past_24_hours()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromHours(23));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.ExtendAsync(fixture.SessionId, TimeSpan.FromHours(2), fixture.CancellationToken));

        var extended = await fixture.Service.ExtendAsync(
            fixture.SessionId, TimeSpan.FromMinutes(30), fixture.CancellationToken);
        Assert.Equal(TimeSpan.FromHours(23.5), extended.Remaining);
    }

    [Fact]
    public async Task Paused_extension_counts_focus_already_consumed_before_the_pause()
    {
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository();
        var id = Guid.NewGuid();
        await repository.SaveAsync(
            new FocusSession(
                id,
                null,
                clock.UtcNow.AddHours(-22),
                null,
                TimeSpan.FromHours(1),
                FocusStatus.Paused,
                clock.UtcNow,
                TimeSpan.FromHours(22)),
            TestContext.Current.CancellationToken);
        var service = new FocusService(repository, clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ExtendAsync(id, TimeSpan.FromHours(2), TestContext.Current.CancellationToken));

        var result = await service.ExtendAsync(
            id,
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromHours(2), result.Remaining);
    }

    [Fact]
    public async Task Extend_rejects_a_paused_remainder_pushed_past_24_hours()
    {
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository();
        var service = new FocusService(repository, clock);
        var id = Guid.NewGuid();
        await repository.SaveAsync(
            new FocusSession(id, null, clock.UtcNow, null, TimeSpan.FromHours(23), FocusStatus.Paused, clock.UtcNow),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ExtendAsync(id, TimeSpan.FromHours(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pause_caps_a_corrupt_far_future_end_time_at_24_hours()
    {
        var clock = new FakeClock("2026-09-11T10:00:00Z");
        var repository = new InMemoryFocusRepository();
        var service = new FocusService(repository, clock);
        var id = Guid.NewGuid();
        await repository.SaveAsync(
            new FocusSession(
                id, null, clock.UtcNow, clock.UtcNow.AddHours(48), TimeSpan.Zero,
                FocusStatus.Running, clock.UtcNow),
            TestContext.Current.CancellationToken);

        var paused = await service.PauseAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromHours(24), paused.Remaining);
        Assert.Equal(TimeSpan.FromHours(24), repository.Sessions[id].RemainingWhenPaused);
    }

    [Theory]
    [InlineData(1499, FocusStatus.EndedEarly)]
    [InlineData(1500, FocusStatus.Completed)]
    [InlineData(1501, FocusStatus.Completed)]
    public async Task End_classifies_the_session_using_its_deadline(int elapsedSeconds, FocusStatus expected)
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        fixture.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        var result = await fixture.Service.EndAsync(fixture.SessionId, fixture.CancellationToken);

        Assert.Equal(expected, result.Status);
        Assert.Equal(TimeSpan.Zero, result.Remaining);
        Assert.Equal(expected, fixture.Repository.Sessions[fixture.SessionId].Status);
        Assert.False(await fixture.Service.CompleteExpiredAsync(fixture.SessionId, fixture.CancellationToken));
    }

    [Fact]
    public async Task Ending_a_paused_session_remains_an_early_end_after_wall_clock_time_elapses()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        await fixture.Service.PauseAsync(fixture.SessionId, fixture.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(2));

        var ended = await fixture.Service.EndAsync(fixture.SessionId, fixture.CancellationToken);

        Assert.Equal(FocusStatus.EndedEarly, ended.Status);
    }

    [Fact]
    public async Task Concurrent_end_and_expiry_cannot_record_an_expired_session_as_ended_early()
    {
        var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
        fixture.Clock.Advance(TimeSpan.FromMinutes(25));
        fixture.Repository.CoordinateReads = true;
        var token = fixture.CancellationToken;

        var end = Task.Run(() => CaptureAsync(() => fixture.Service.EndAsync(fixture.SessionId, token)));
        var expiry = Task.Run(() => fixture.Service.CompleteExpiredAsync(fixture.SessionId, token));
        await Task.WhenAll(end, expiry);
        var endResult = await end;
        var expiryResult = await expiry;

        Assert.Equal(FocusStatus.Completed, fixture.Repository.Sessions[fixture.SessionId].Status);
        Assert.Equal(2, fixture.Repository.CompareAndSetAttempts);
        Assert.Equal(!expiryResult, endResult.Succeeded);
        if (endResult.Succeeded)
        {
            Assert.Equal(FocusStatus.Completed, endResult.Snapshot!.Status);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(endResult.Exception);
        }
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

        public Task<bool> TryCompareAndSetAsync(TaskItem expected, TaskItem replacement, CancellationToken cancellationToken)
        {
            if (!Tasks.TryGetValue(expected.Id, out var current) || current != expected)
            {
                return Task.FromResult(false);
            }

            Tasks[replacement.Id] = replacement;
            return Task.FromResult(true);
        }
    }
}
