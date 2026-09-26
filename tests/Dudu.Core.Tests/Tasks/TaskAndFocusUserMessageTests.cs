using Dudu.Core.Abstractions;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Tasks;

/// <summary>These exception messages are shown verbatim on the Tasks &amp; Focus
/// page (FeatureViewModelBase.ToUserMessage passes ArgumentException,
/// KeyNotFoundException and InvalidOperationException text through), so they
/// must be plain, friendly copy: no GUIDs, no enum names, no framework
/// jargon like "Unicode scalar values".</summary>
public sealed class TaskAndFocusUserMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Blank_and_overlong_task_titles_use_friendly_copy()
    {
        var service = new TaskService(new Repository(), new Clock());

        var blank = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("   ", Ct));
        Assert.StartsWith("aiyo add a title first", blank.Message);

        var overlong = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new string('a', 121), Ct));
        Assert.StartsWith("keep the title under 120 characters", overlong.Message);
        Assert.DoesNotContain("Unicode", overlong.Message);
    }

    [Fact]
    public async Task A_missing_task_does_not_show_its_guid()
    {
        var service = new TaskService(new Repository(), new Clock());
        var id = Guid.NewGuid();

        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CompleteAsync(id, Ct));

        Assert.DoesNotContain(id.ToString(), missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("oh no that task is gone refresh and try again", missing.Message);
    }

    [Fact]
    public async Task An_invalid_focus_transition_names_the_state_in_plain_words()
    {
        var repository = new FocusRepository();
        var service = new FocusService(repository, new Clock());
        var started = await service.StartAsync(null, TimeSpan.FromMinutes(25), Ct);
        await service.PauseAsync(started.Id, Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PauseAsync(started.Id, Ct));
        Assert.Equal("aiyo cant pause focus while it is paused", error.Message);

        await service.EndAsync(started.Id, Ct);
        var ended = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(started.Id, Ct));
        Assert.Equal("aiyo cant resume focus while it is already ended", ended.Message);
        Assert.DoesNotContain("EndedEarly", ended.Message);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-12T10:00:00Z");
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Repository : ITaskRepository
    {
        private readonly Dictionary<Guid, TaskItem> _tasks = [];
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_tasks.GetValueOrDefault(id));
        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.Values.ToArray());
        public Task<IReadOnlyList<TaskItem>> ListCompletedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>([]);
        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken) { _tasks[task.Id] = task; return Task.CompletedTask; }
        public Task<bool> TryCompareAndSetAsync(TaskItem expected, TaskItem replacement, CancellationToken cancellationToken) { _tasks[replacement.Id] = replacement; return Task.FromResult(true); }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) { _tasks.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FocusRepository : IFocusSessionRepository
    {
        private readonly Dictionary<Guid, FocusSession> _sessions = [];
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.Values.FirstOrDefault(item => item.Status is FocusStatus.Running or FocusStatus.Paused));
        public Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FocusSession>>([]);
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken) { _sessions[session.Id] = session; return Task.FromResult(true); }
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken) { _sessions[expected.Id] = replacement; return Task.FromResult(true); }
        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken) { _sessions[session.Id] = session; return Task.CompletedTask; }
    }
}
