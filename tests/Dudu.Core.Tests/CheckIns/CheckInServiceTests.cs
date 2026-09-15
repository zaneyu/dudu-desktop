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
    }
}
