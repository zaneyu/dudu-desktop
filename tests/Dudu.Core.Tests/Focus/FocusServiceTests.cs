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
        public Dictionary<Guid, FocusSession> Sessions { get; } = [];

        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.GetValueOrDefault(id));

        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.Values.FirstOrDefault(session =>
                session.Status is FocusStatus.Running or FocusStatus.Paused));

        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            Sessions[session.Id] = session;
            return Task.CompletedTask;
        }
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
