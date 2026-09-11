using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.CheckIns;
using Dudu.Core.Countdowns;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Companion;

public sealed class CompanionFeatureTests
{
    [Fact]
    public async Task Local_note_selector_respects_daily_cap_and_recent_history()
    {
        var fixture = NoteFixture.WithNotes("one", "two", "three");
        await fixture.RecordShownAsync("one", count: 3);

        var result = await fixture.Selector.SelectAsync(
            manualRequest: false,
            fixture.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_in_summary_never_calls_a_remote_dependency()
    {
        var repository = new SpyCheckInRepository();
        var service = new CheckInService(
            repository,
            new FakeClock("2026-09-11T10:00:00Z"));

        await service.RecordAsync(
            MoodChoice.Tired,
            "long day",
            TestContext.Current.CancellationToken);

        var summary = await service.SummarizeAsync(
            7,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Counts[MoodChoice.Tired]);
        Assert.Equal(0, repository.RemoteCallCount);
    }

    [Fact]
    public void Automatic_outfit_uses_anniversary_then_base_fallback()
    {
        var selected = SeasonalOutfitPolicy.Select(
            new DateOnly(2026, 9, 11),
            new SeasonalDates(Anniversary: new MonthDay(9, 11), Birthday: null),
            availableKeys: ["base", "anniversary"]);

        Assert.Equal("anniversary", selected);
    }

    [Fact]
    public Task Manual_request_bypasses_unsolicited_cap() =>
        CompanionAssertions.ManualRequestReturnsNoteAfterCapAsync();

    [Fact]
    public Task Selector_excludes_two_most_recent_ids() =>
        CompanionAssertions.RecentIdsAreExcludedAsync(count: 2);

    [Fact]
    public void Past_countdown_is_zero() =>
        Assert.Equal(TimeSpan.Zero, CompanionFixtures.PastCountdown().Remaining);

    [Fact]
    public void Missing_seasonal_art_falls_back_to_base() =>
        Assert.Equal("base", SeasonalOutfitPolicy.Select(
            new DateOnly(2026, 12, 25),
            SeasonalDates.Empty,
            ["base"]));

    [Fact]
    public async Task Selector_persists_utc_shown_event_after_selection()
    {
        var fixture = NoteFixture.WithNotes("one");

        var result = await fixture.Selector.SelectAsync(
            manualRequest: false,
            fixture.CancellationToken);

        Assert.Equal("one", result!.Id);
        var shown = Assert.Single(fixture.Repository.Shown);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T10:00:00Z"), shown.ShownUtc);
        Assert.True(shown.IsUnsolicited);
    }

    [Fact]
    public async Task Check_in_summary_uses_local_dates_and_normalizes_utc()
    {
        var clock = new FakeClock("2026-09-11T00:30:00-07:00")
        {
            LocalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "local",
                TimeSpan.FromHours(-7),
                "local",
                "local"),
        };
        var repository = new SpyCheckInRepository();
        var service = new CheckInService(repository, clock);

        var checkIn = await service.RecordAsync(
            MoodChoice.Great,
            "  rested  ",
            TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.Parse("2026-09-11T07:30:00Z"), checkIn.CreatedUtc);
        Assert.Equal("rested", checkIn.Note);
        Assert.Equal(1, (await service.SummarizeAsync(
            1,
            TestContext.Current.CancellationToken)).Counts[MoodChoice.Great]);
    }

    [Fact]
    public void Countdown_uses_calendar_days_for_all_day_dates()
    {
        var countdown = new Countdown(
            "birthday",
            "Birthday",
            new DateOnly(2026, 9, 13));

        var display = CountdownService.GetDisplay(
            countdown,
            DateTimeOffset.Parse("2026-09-11T23:59:00Z"));

        Assert.Equal(2, display.CalendarDays);
        Assert.Equal(TimeSpan.FromDays(2), display.Remaining);
    }

    private sealed class NoteFixture
    {
        private NoteFixture(
            InMemoryLocalNoteRepository repository,
            LocalNoteSelector selector,
            FakeClock clock)
        {
            Repository = repository;
            Selector = selector;
            Clock = clock;
        }

        public InMemoryLocalNoteRepository Repository { get; }
        public LocalNoteSelector Selector { get; }
        public FakeClock Clock { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public static NoteFixture WithNotes(params string[] ids)
        {
            var clock = new FakeClock("2026-09-11T10:00:00Z");
            var repository = new InMemoryLocalNoteRepository(
                ids.Select(id => new LocalLoveNote(id, id, true)).ToArray());
            var preferences = new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false,
                3,
                false,
                false,
                false,
                TimeSpan.FromMinutes(15));
            var selector = new LocalNoteSelector(
                repository,
                clock,
                new FixedRandomSource(),
                preferences);
            return new NoteFixture(repository, selector, clock);
        }

        public async Task RecordShownAsync(string id, int count)
        {
            for (var index = 0; index < count; index++)
            {
                await Repository.RecordShownAsync(
                    id,
                    Clock.UtcNow,
                    unsolicited: true,
                    CancellationToken);
            }
        }
    }

    private sealed class CompanionAssertions
    {
        public static async Task ManualRequestReturnsNoteAfterCapAsync()
        {
            var fixture = NoteFixture.WithNotes("manual", "other", "third");
            await fixture.RecordShownAsync("manual", count: 3);

            var result = await fixture.Selector.SelectAsync(
                manualRequest: true,
                fixture.CancellationToken);

            Assert.NotNull(result);
        }

        public static async Task RecentIdsAreExcludedAsync(int count)
        {
            var fixture = NoteFixture.WithNotes("one", "two", "three");
            await fixture.Repository.RecordShownAsync(
                "one",
                fixture.Clock.UtcNow,
                unsolicited: false,
                fixture.CancellationToken);
            await fixture.Repository.RecordShownAsync(
                "two",
                fixture.Clock.UtcNow.AddMinutes(1),
                unsolicited: false,
                fixture.CancellationToken);

            var result = await fixture.Selector.SelectAsync(
                manualRequest: true,
                fixture.CancellationToken);

            Assert.Equal("three", result!.Id);
            Assert.Equal(count, await fixture.Repository.RecentIdsCountAsync(count));
        }
    }

    private static class CompanionFixtures
    {
        public static CountdownDisplay PastCountdown() =>
            CountdownService.GetDisplay(
                new Countdown("past", "Past", new DateOnly(2026, 9, 10)),
                DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
    }

    private sealed class FakeClock(string initialUtc) : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse(initialUtc).ToUniversalTime();

        public TimeZoneInfo LocalTimeZone { get; init; } = TimeZoneInfo.Utc;
    }

    private sealed class FixedRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    private sealed class InMemoryLocalNoteRepository(
        IReadOnlyList<LocalLoveNote> notes) : ILocalNoteRepository
    {
        private readonly List<ShownNote> _shown = [];

        public IReadOnlyList<ShownNote> Shown => _shown;

        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LocalLoveNote>>(
                notes.Where(note => note.Enabled).ToArray());
        }

        public Task<int> CountUnsolicitedShownAsync(
            DateOnly localDate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_shown.Count(item => item.IsUnsolicited));
        }

        public Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(_shown
                .OrderByDescending(item => item.ShownUtc)
                .Take(count)
                .Select(item => item.NoteId)
                .ToArray());
        }

        public Task RecordShownAsync(
            string noteId,
            DateTimeOffset shownUtc,
            bool unsolicited,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _shown.Add(new ShownNote(noteId, shownUtc, unsolicited));
            return Task.CompletedTask;
        }

        public Task<int> RecentIdsCountAsync(int count) =>
            Task.FromResult(_shown.OrderByDescending(item => item.ShownUtc).Take(count).Count());
    }

    private sealed class SpyCheckInRepository : ICheckInRepository
    {
        private readonly List<MoodCheckIn> _checkIns = [];

        public int RemoteCallCount { get; private set; }

        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _checkIns.Add(checkIn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MoodCheckIn>>(_checkIns
                .Where(checkIn => checkIn.CreatedUtc >= sinceUtc)
                .ToArray());
        }
    }

    private sealed record ShownNote(string NoteId, DateTimeOffset ShownUtc, bool IsUnsolicited);
}
