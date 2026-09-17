using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.CheckIns;

public sealed class CheckInServiceTests
{
    [Fact]
    public async Task Record_rejects_notes_longer_than_2000_unicode_scalars()
    {
        var service = new CheckInService(
            new InMemoryCheckInRepository(),
            new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z")));

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(
            MoodChoice.Okay,
            string.Concat(Enumerable.Repeat("😀", 2_001)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Record_accepts_a_note_at_exactly_2000_unicode_scalars()
    {
        var repository = new InMemoryCheckInRepository();
        var service = new CheckInService(
            repository,
            new FakeClock(DateTimeOffset.Parse("2026-09-11T10:00:00Z")));
        var note = string.Concat(Enumerable.Repeat("😀", 2_000));

        var checkIn = await service.RecordAsync(
            MoodChoice.Great,
            note,
            TestContext.Current.CancellationToken);

        Assert.Equal(note, checkIn.Note);
        Assert.Single(repository.Saved);
    }

    [Fact]
    public async Task Summary_includes_check_ins_from_both_sides_of_an_ambiguous_midnight()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 9, 12));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "ambiguous-local", TimeSpan.FromHours(1), "local", "standard", "daylight", [rule]);
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 9, 12, 0, 0, 0)));
        var clock = new MutableClock(DateTimeOffset.Parse("2026-09-11T22:30:00Z")) { LocalTimeZone = zone };
        var repository = new InMemoryCheckInRepository();
        var service = new CheckInService(repository, clock);
        var token = TestContext.Current.CancellationToken;

        await service.RecordAsync(MoodChoice.Great, token);
        clock.UtcNow = DateTimeOffset.Parse("2026-09-11T23:30:00Z");
        await service.RecordAsync(MoodChoice.Tired, token);
        clock.UtcNow = DateTimeOffset.Parse("2026-09-12T12:00:00Z");

        var summary = await service.SummarizeAsync(1, token);

        Assert.Equal(1, summary.Counts[MoodChoice.Great]);
        Assert.Equal(1, summary.Counts[MoodChoice.Tired]);
        Assert.Equal(2, summary.Recent.Count);
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class InMemoryCheckInRepository : ICheckInRepository
    {
        public List<MoodCheckIn> Saved { get; } = [];

        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken)
        {
            Saved.Add(checkIn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodCheckIn>>(
                Saved.Where(checkIn => checkIn.CreatedUtc >= sinceUtc).ToArray());
    }}
