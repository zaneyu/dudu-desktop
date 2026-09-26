using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Home's focus line used to be computed once per refresh, so a running session
/// read "25 min left" for as long as Home stayed open. It now counts down from the time
/// the snapshot was read, the same way the Tasks and Focus page does.</summary>
public sealed class HomeFocusCountdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Running_focus_counts_down_between_refreshes()
    {
        var focus = new MemoryFocusRepository();
        var fixture = SettingsDataPagesFixture.Create(focusSessions: focus);
        await fixture.Context.FocusService.StartAsync(null, TimeSpan.FromMinutes(25), Ct);
        var viewModel = new HomeViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("focus is running with 25 min left", viewModel.ActiveFocusText);

        var announced = new List<string?>();
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(10).AddSeconds(30);
        viewModel.RefreshFocusCountdown();

        Assert.Contains(nameof(HomeViewModel.ActiveFocusText), announced);
        Assert.Equal(TimeSpan.FromMinutes(14.5), viewModel.ActiveFocusRemaining);
        Assert.Equal("focus is running with 15 min left", viewModel.ActiveFocusText);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(2);
        viewModel.RefreshFocusCountdown();
        Assert.Equal(TimeSpan.Zero, viewModel.ActiveFocusRemaining);
        Assert.Equal("focus is running with 0 min left", viewModel.ActiveFocusText);
    }

    [Fact]
    public async Task Paused_focus_holds_still()
    {
        var focus = new MemoryFocusRepository();
        var fixture = SettingsDataPagesFixture.Create(focusSessions: focus);
        var started = await fixture.Context.FocusService.StartAsync(null, TimeSpan.FromMinutes(90), Ct);
        await fixture.Context.FocusService.PauseAsync(started.Id, Ct);
        var viewModel = new HomeViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(40);
        viewModel.RefreshFocusCountdown();

        Assert.Equal("focus is paused with 1 hr 30 min left", viewModel.ActiveFocusText);
    }

    [Fact]
    public async Task A_refresh_restarts_the_countdown_from_the_new_snapshot()
    {
        var focus = new MemoryFocusRepository();
        var fixture = SettingsDataPagesFixture.Create(focusSessions: focus);
        await fixture.Context.FocusService.StartAsync(null, TimeSpan.FromMinutes(25), Ct);
        var viewModel = new HomeViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(5);
        await viewModel.RefreshAsync(Ct);
        Assert.Equal("focus is running with 20 min left", viewModel.ActiveFocusText);

        // Elapsed time is measured from the latest snapshot, not counted twice.
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(5);
        viewModel.RefreshFocusCountdown();
        Assert.Equal("focus is running with 15 min left", viewModel.ActiveFocusText);
    }

    [Fact]
    public void RemainingAt_never_goes_negative_and_ignores_clock_skew()
    {
        var captured = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var running = new FocusSnapshot(Guid.NewGuid(), null, FocusStatus.Running, TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(10), FocusDisplay.RemainingAt(running, captured, captured.AddMinutes(-3)));
        Assert.Equal(TimeSpan.FromMinutes(4), FocusDisplay.RemainingAt(running, captured, captured.AddMinutes(6)));
        Assert.Equal(TimeSpan.Zero, FocusDisplay.RemainingAt(running, captured, captured.AddHours(1)));
        Assert.Equal(
            TimeSpan.Zero,
            FocusDisplay.RemainingAt(running with { Status = FocusStatus.Completed }, captured, captured));
        Assert.Equal(TimeSpan.Zero, FocusDisplay.RemainingAt(null, captured, captured));
    }

    /// <summary>Keeps one active session, enough for FocusService start/pause/read.</summary>
    private sealed class MemoryFocusRepository : IFocusSessionRepository
    {
        private FocusSession? _active;

        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_active?.Id == id ? _active : null);

        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_active is { Status: FocusStatus.Running or FocusStatus.Paused } ? _active : null);

        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            if (_active is { Status: FocusStatus.Running or FocusStatus.Paused }) return Task.FromResult(false);
            _active = session;
            return Task.FromResult(true);
        }

        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken)
        {
            if (_active != expected) return Task.FromResult(false);
            _active = replacement;
            return Task.FromResult(true);
        }

        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            _active = session;
            return Task.CompletedTask;
        }
    }
}
