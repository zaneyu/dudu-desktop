using Dudu.Core.Assets;
using Xunit;

namespace Dudu.Core.Tests.Companion;

public sealed class CompanionFeatureTests
{
    [Fact]
    public void Animation_resolution_always_uses_the_base_outfit()
    {
        var baseAnimation = Animation("base-celebrate");
        var baseIdle = Animation("base-idle");
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
                            ["idle"] = baseIdle,
                            ["celebrate"] = baseAnimation,
                        },
                    },
                    ["winter"] = new AssetOutfit
                    {
                        Animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal)
                        {
                            ["idle"] = Animation("winter-idle"),
                            ["celebrate"] = Animation("winter-celebrate"),
                            ["sleep"] = Animation("winter-sleep"),
                        },
                    },
                },
            });

        // Other outfits stay valid in the manifest, but playback never picks
        // them: a key base has resolves from base, and a key only another
        // outfit has falls back to base idle, not that outfit's art.
        Assert.Same(baseAnimation, pack.ResolveAnimation("celebrate"));
        Assert.Same(baseIdle, pack.ResolveAnimation("sleep"));
        Assert.Same(baseIdle, pack.ResolveAnimation("idle"));
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
