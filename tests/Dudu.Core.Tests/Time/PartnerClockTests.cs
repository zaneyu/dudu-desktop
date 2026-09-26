using Dudu.Core.Time;
using Xunit;

namespace Dudu.Core.Tests.Time;

public sealed class PartnerClockTests
{
    private static readonly TimeZoneInfo Singapore =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-8", TimeSpan.FromHours(8), "test +8", "test +8");

    public static TheoryData<string> PartnerZones => new() { "system", "built-in" };

    [Fact]
    public void Partner_zone_is_a_single_uk_constant_with_a_windows_fallback_id()
    {
        Assert.Equal("Europe/London", PartnerClock.PartnerZoneId);
        Assert.Equal("GMT Standard Time", PartnerClock.PartnerZoneWindowsId);
    }

    [Theory]
    [MemberData(nameof(PartnerZones))]
    public void Summer_reads_bst_gmt_plus_one_and_the_difference_from_local(string zone)
    {
        var reading = PartnerClock.Read(At("2026-07-15T13:05:30Z"), Zone(zone), Singapore);

        Assert.True(reading.IsDaylightSaving);
        Assert.Equal(TimeSpan.FromHours(1), reading.Offset);
        Assert.Equal("14:05", reading.TimeText);
        Assert.Equal(":30", reading.SecondsText);
        Assert.Equal("wednesday 15 jul", reading.DateText);
        Assert.Equal("BST · GMT+1", reading.OffsetLabel);
        Assert.Equal("7 h behind you", reading.DifferenceText);
        Assert.Equal("same day as you", reading.DayText);
        Assert.Equal("UK 14:05", reading.OverlayLabel);
        Assert.Equal("☀️", reading.DayPeriodEmoji);
        Assert.Equal("time in the UK 14:05, BST GMT+1, 7 h behind you", reading.SpokenText);
    }

    [Theory]
    [MemberData(nameof(PartnerZones))]
    public void Winter_reads_gmt_plus_zero(string zone)
    {
        var reading = PartnerClock.Read(At("2026-12-01T09:00:00Z"), Zone(zone), Singapore);

        Assert.False(reading.IsDaylightSaving);
        Assert.Equal(TimeSpan.Zero, reading.Offset);
        Assert.Equal("09:00", reading.TimeText);
        Assert.Equal("GMT · GMT+0", reading.OffsetLabel);
        Assert.Equal("8 h behind you", reading.DifferenceText);
        Assert.Equal("tuesday 1 dec", reading.DateText);
    }

    [Theory]
    [MemberData(nameof(PartnerZones))]
    public void Spring_forward_on_2026_03_29_jumps_from_01_00_gmt_to_02_00_bst(string zone)
    {
        var before = PartnerClock.Read(At("2026-03-29T00:59:59Z"), Zone(zone), Singapore);
        var after = PartnerClock.Read(At("2026-03-29T01:00:00Z"), Zone(zone), Singapore);

        Assert.Equal("00:59", before.TimeText);
        Assert.Equal("GMT · GMT+0", before.OffsetLabel);
        Assert.Equal("8 h behind you", before.DifferenceText);
        Assert.Equal("02:00", after.TimeText);
        Assert.Equal("BST · GMT+1", after.OffsetLabel);
        Assert.Equal("7 h behind you", after.DifferenceText);
    }

    [Theory]
    [MemberData(nameof(PartnerZones))]
    public void Fall_back_on_2026_10_25_repeats_01_00_as_gmt(string zone)
    {
        var before = PartnerClock.Read(At("2026-10-25T00:59:00Z"), Zone(zone), Singapore);
        var after = PartnerClock.Read(At("2026-10-25T01:00:00Z"), Zone(zone), Singapore);

        Assert.Equal("01:59", before.TimeText);
        Assert.Equal("BST · GMT+1", before.OffsetLabel);
        Assert.True(before.IsDaylightSaving);
        Assert.Equal("01:00", after.TimeText);
        Assert.Equal("GMT · GMT+0", after.OffsetLabel);
        Assert.False(after.IsDaylightSaving);
        Assert.Equal("sunday 25 oct", after.DateText);
    }

    [Fact]
    public void Day_text_reports_when_the_partner_is_on_a_different_calendar_day()
    {
        var behind = PartnerClock.Read(At("2026-07-15T17:00:00Z"), PartnerClock.CreateFallbackUkZone(), Singapore);
        var newYork = TimeZoneInfo.CreateCustomTimeZone("test-minus-5", TimeSpan.FromHours(-5), "test -5", "test -5");
        var ahead = PartnerClock.Read(At("2026-07-15T23:30:00Z"), PartnerClock.CreateFallbackUkZone(), newYork);

        Assert.Equal(-1, behind.DayDelta);
        Assert.Equal("still yesterday there", behind.DayText);
        Assert.Equal(1, ahead.DayDelta);
        Assert.Equal("already tomorrow there", ahead.DayText);
        Assert.Equal("6 h ahead of you", ahead.DifferenceText);
    }

    [Fact]
    public void Difference_handles_half_hour_zones_and_the_same_zone()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("test-plus-5-30", new TimeSpan(5, 30, 0), "test +5:30", "test +5:30");
        var uk = PartnerClock.CreateFallbackUkZone();

        Assert.Equal("4 h 30 min behind you", PartnerClock.Read(At("2026-07-15T12:00:00Z"), uk, india).DifferenceText);
        Assert.Equal("same time as you", PartnerClock.Read(At("2026-07-15T12:00:00Z"), uk, uk).DifferenceText);
    }

    [Theory]
    [InlineData("2026-07-15T02:00:00Z", true, "probably fast asleep 💤")]
    [InlineData("2026-07-15T06:30:00Z", false, "morning there ☕")]
    [InlineData("2026-07-15T12:00:00Z", false, "daytime there")]
    [InlineData("2026-07-15T18:00:00Z", false, "evening there 🌇")]
    [InlineData("2026-07-15T21:30:00Z", true, "getting late there 🌙")]
    public void Mood_and_night_follow_the_partner_hour(string utc, bool night, string mood)
    {
        var reading = PartnerClock.Read(At(utc), PartnerClock.CreateFallbackUkZone(), Singapore);

        Assert.Equal(night, reading.IsNight);
        Assert.Equal(night ? "🌙" : "☀️", reading.DayPeriodEmoji);
        Assert.Equal(mood, reading.MoodText);
    }

    [Fact]
    public void Now_reads_the_injected_clock()
    {
        var clock = new FakeClock(At("2026-01-10T20:15:00Z"), Singapore);
        var partner = new PartnerClock(clock, PartnerClock.CreateFallbackUkZone());

        var reading = partner.Now();

        Assert.Equal("20:15", reading.TimeText);
        Assert.Equal("GMT · GMT+0", reading.OffsetLabel);
        Assert.Equal("still yesterday there", reading.DayText);
    }

    [Fact]
    public void Resolution_prefers_iana_then_windows_id_then_built_in_rules()
    {
        var requested = new List<string>();
        var viaWindowsId = PartnerClock.ResolvePartnerZone(id =>
        {
            requested.Add(id);
            return id == PartnerClock.PartnerZoneWindowsId
                ? PartnerClock.CreateFallbackUkZone()
                : throw new TimeZoneNotFoundException();
        });
        var builtIn = PartnerClock.ResolvePartnerZone(_ => throw new InvalidTimeZoneException());

        Assert.Equal([PartnerClock.PartnerZoneId, PartnerClock.PartnerZoneWindowsId], requested);
        Assert.NotNull(viaWindowsId);
        Assert.StartsWith(PartnerClock.PartnerZoneId, builtIn.Id, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromHours(1), builtIn.GetUtcOffset(At("2026-07-15T12:00:00Z")));
    }

    private static TimeZoneInfo Zone(string kind) => kind == "system"
        ? PartnerClock.ResolvePartnerZone()
        : PartnerClock.CreateFallbackUkZone();

    private static DateTimeOffset At(string utc) =>
        DateTimeOffset.Parse(utc, global::System.Globalization.CultureInfo.InvariantCulture);

    private sealed class FakeClock(DateTimeOffset now, TimeZoneInfo zone) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public TimeZoneInfo LocalTimeZone { get; } = zone;
    }
}
