using Dudu.Core.Assets;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests.Assets;

/// <summary>
/// A missing sticker/pose must still resolve to a visible idle frame but now
/// reports the substitution through the fallback hook, so "wrong pose"
/// reports are traceable instead of silent.
/// </summary>
public sealed class AssetPackFallbackTests
{
    [Fact]
    public void Missing_sticker_reports_fallback_and_returns_base_idle()
    {
        var pack = PackWithBaseIdle();
        string? diagnostic = null;

        var resolved = pack.ResolveAnimation(
            "sticker-018",
            new DateOnly(2026, 9, 25),
            SeasonalDates.Empty,
            onFallback: message => diagnostic = message);

        Assert.Equal("idle", resolved.Frames[0].File);
        Assert.NotNull(diagnostic);
        Assert.Contains("sticker-018", diagnostic);
    }

    [Fact]
    public void Exact_animation_hit_reports_no_fallback()
    {
        var pack = PackWithBaseIdle();
        var reported = false;

        var resolved = pack.ResolveAnimation(
            "idle",
            new DateOnly(2026, 9, 25),
            SeasonalDates.Empty,
            onFallback: _ => reported = true);

        Assert.False(reported);
        Assert.Equal("idle", resolved.Frames[0].File);
    }

    [Fact]
    public void Outfit_missing_pose_falls_back_to_base_with_diagnostic()
    {
        var manifest = new AssetManifest
        {
            SchemaVersion = AssetManifestContract.CurrentSchemaVersion,
            PackId = "test",
            Version = "1",
            Outfits =
            {
                ["base"] = OutfitWith("idle", "greeting"),
                ["party"] = OutfitWith("idle"),
            },
        };
        var pack = new AssetPack("/tmp/manifest.json", manifest);
        string? diagnostic = null;

        var resolved = pack.ResolveAnimation(
            "greeting",
            new DateOnly(2026, 9, 25),
            SeasonalDates.Empty,
            manualOutfit: "party",
            onFallback: message => diagnostic = message);

        Assert.Equal("greeting", resolved.Frames[0].File);
        Assert.NotNull(diagnostic);
        Assert.Contains("party", diagnostic);
    }

    private static AssetPack PackWithBaseIdle()
    {
        var manifest = new AssetManifest
        {
            SchemaVersion = AssetManifestContract.CurrentSchemaVersion,
            PackId = "test",
            Version = "1",
            Outfits =
            {
                ["base"] = OutfitWith("idle"),
            },
        };
        return new AssetPack("/tmp/manifest.json", manifest);
    }

    private static AssetOutfit OutfitWith(params string[] animationKeys)
    {
        var outfit = new AssetOutfit();
        foreach (var key in animationKeys)
        {
            outfit.Animations[key] = new AssetAnimation
            {
                Frames = [new AssetFrame { File = key, DurationMs = 100 }],
                Loop = "loop",
                NominalSize = new PixelSize(128, 128),
            };
        }

        return outfit;
    }
}
