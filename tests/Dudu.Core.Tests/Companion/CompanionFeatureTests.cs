using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.CheckIns;
using Dudu.Core.Countdowns;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Companion;

public sealed class CompanionFeatureTests
{
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
    public void Leap_day_anniversary_matches_feb_28_in_non_leap_years()
    {
        Assert.True(new MonthDay(2, 29).Matches(new DateOnly(2024, 2, 29)));
        Assert.True(new MonthDay(2, 29).Matches(new DateOnly(2025, 2, 28)));
        Assert.False(new MonthDay(2, 29).Matches(new DateOnly(2025, 3, 1)));
        Assert.False(new MonthDay(2, 28).Matches(new DateOnly(2025, 2, 27)));

        var selected = SeasonalOutfitPolicy.Select(
            new DateOnly(2025, 2, 28),
            new SeasonalDates(Anniversary: new MonthDay(2, 29), Birthday: null),
            availableKeys: ["base", "anniversary"]);

        Assert.Equal("anniversary", selected);
    }

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
    public void Seasonal_outfit_uses_base_animation_when_only_its_idle_pose_is_custom()
    {
        var baseAnimation = Animation("base-celebrate");
        var pack = new AssetPack(
            "fixture/manifest.json",
            new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture",
                Version = "1",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "fixture" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            ["idle"] = Animation("base-idle"),
                            ["celebrate"] = baseAnimation,
                        },
                    },
                    ["winter"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            ["idle"] = Animation("winter-idle"),
                        },
                    },
                },
            });

        var selected = pack.ResolveAnimation(
            "celebrate",
            new DateOnly(2026, 12, 25),
            SeasonalDates.Empty);

        Assert.Same(baseAnimation, selected);
    }

    private static AssetAnimation Animation(string file) => new()
    {
        Frames = [new AssetFrame { File = file + ".png", DurationMs = 100 }],
        Loop = "loop",
        Anchor = new PixelPoint(0, 0),
        NominalSize = new PixelSize(1, 1),
        ReducedMotion = file + ".png",
    };

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

    [Fact]
    public void Countdown_uses_configured_local_date_when_utc_date_differs()
    {
        var localTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "local",
            TimeSpan.FromHours(-8),
            "local",
            "local");
        var countdown = new Countdown(
            "birthday",
            "Birthday",
            new DateOnly(2026, 9, 12),
            localTimeZone);

        var display = CountdownService.GetDisplay(
            countdown,
            DateTimeOffset.Parse("2026-09-12T01:00:00Z"));

        Assert.Equal(1, display.CalendarDays);
        Assert.Equal(TimeSpan.FromDays(1), display.Remaining);
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

        public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;
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

}
