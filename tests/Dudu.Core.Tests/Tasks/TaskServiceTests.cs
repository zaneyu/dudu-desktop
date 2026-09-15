using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Tasks;

public sealed class TaskServiceTests
{
    [Fact]
    public async Task Create_trims_title_and_persists_utc_timestamps()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00-07:00"));
        var repository = new InMemoryTaskRepository();
        var service = new TaskService(repository, clock);

        var task = await service.CreateAsync(
            "  Write report  ",
            "notes",
            DateTimeOffset.Parse("2026-09-12T09:00:00-07:00"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Write report", task.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T17:00:00Z"), task.CreatedUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T16:00:00Z"), task.DueUtc);
        Assert.Equal(task, repository.Tasks[task.Id]);
    }

    [Fact]
    public async Task Create_rejects_empty_or_oversized_unicode_input()
    {
        var service = new TaskService(new InMemoryTaskRepository(), new FakeClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(" \t", null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(new string('a', 121), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync("ok", string.Concat(Enumerable.Repeat("😀", 2_001)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Complete_is_idempotent_and_active_list_excludes_completed_task()
    {
        var repository = new InMemoryTaskRepository();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var service = new TaskService(repository, clock);
        var task = await service.CreateAsync("Task", null, TestContext.Current.CancellationToken);

        var completed = await service.CompleteAsync(task.Id, TestContext.Current.CancellationToken);
        var repeated = await service.CompleteAsync(task.Id, TestContext.Current.CancellationToken);

        Assert.True(completed.IsCompleted);
        Assert.Equal(completed.CompletedUtc, repeated.CompletedUtc);
        Assert.Empty(await service.ListActiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stale_update_cannot_reopen_a_task_completed_after_it_was_loaded()
    {
        var repository = new InMemoryTaskRepository();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var service = new TaskService(repository, clock);
        var task = await service.CreateAsync("Task", null, TestContext.Current.CancellationToken);
        repository.BeforeCompareAndSet = () =>
        {
            repository.Tasks[task.Id] = task with
            {
                IsCompleted = true,
                CompletedUtc = clock.UtcNow,
                UpdatedUtc = clock.UtcNow,
            };
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            task.Id, "Edited", null, TestContext.Current.CancellationToken));

        Assert.True(repository.Tasks[task.Id].IsCompleted);
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class InMemoryTaskRepository : ITaskRepository
    {
        public Dictionary<Guid, TaskItem> Tasks { get; } = [];
        public Action? BeforeCompareAndSet { get; set; }

        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Tasks.GetValueOrDefault(id));

        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskItem>>(
                Tasks.Values.Where(task => !task.IsCompleted).ToArray());

        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task<bool> TryCompareAndSetAsync(TaskItem expected, TaskItem replacement, CancellationToken cancellationToken)
        {
            BeforeCompareAndSet?.Invoke();
            BeforeCompareAndSet = null;
            if (!Tasks.TryGetValue(expected.Id, out var current) || current != expected)
            {
                return Task.FromResult(false);
            }

            Tasks[replacement.Id] = replacement;
            return Task.FromResult(true);
        }
    }
}
