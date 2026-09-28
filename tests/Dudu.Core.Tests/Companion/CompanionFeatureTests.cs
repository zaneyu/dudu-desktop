using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests.Companion;

public sealed class CompanionFeatureTests
{
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
}
