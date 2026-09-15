using Dudu.Core.Assets;
using Xunit;

namespace Dudu.Core.Tests.Assets;

public sealed class AssetManifestContractTests
{
    [Theory]
    [InlineData("evil pack!")]
    [InlineData("../escape")]
    [InlineData("pack/sub")]
    [InlineData(" pack ")]
    public void Pack_id_must_be_a_safe_identifier(string packId)
    {
        var manifest = ValidManifest(packId);

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("packId", StringComparison.Ordinal));
    }

    [Fact]
    public void Frame_count_is_capped_per_animation()
    {
        var manifest = ValidManifest();
        manifest.Outfits["base"].Animations["idle"] = Animation(
            "idle",
            frames: Enumerable.Range(0, AssetManifestContract.MaxFramesPerAnimation + 1)
                .Select(index => new AssetFrame
                {
                    File = $"frame-{index}.png",
                    DurationMs = 10,
                    Sha256 = new string('a', 64),
                })
                .ToList());

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("at most", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_frame_duration_is_capped_at_ten_seconds()
    {
        var manifest = ValidManifest();
        manifest.Outfits["base"].Animations["idle"] = Animation(
            "idle",
            frames:
            [
                new AssetFrame
                {
                    File = "pose.png",
                    DurationMs = AssetManifestContract.MaxFrameDurationMs + 1,
                    Sha256 = new string('a', 64),
                },
            ]);

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("must not exceed", StringComparison.Ordinal));
    }

    [Fact]
    public void Total_animation_duration_is_capped_at_ten_seconds()
    {
        var manifest = ValidManifest();
        manifest.Outfits["base"].Animations["idle"] = Animation(
            "idle",
            frames:
            [
                new AssetFrame { File = "a.png", DurationMs = 6_000, Sha256 = new string('a', 64) },
                new AssetFrame { File = "b.png", DurationMs = 6_000, Sha256 = new string('a', 64) },
            ]);

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("total duration", StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_manifest_within_caps_passes()
    {
        Assert.Empty(AssetManifestContract.Validate(ValidManifest()));
    }

    private static AssetManifest ValidManifest(string packId = "fixture-pack") => new()
    {
        SchemaVersion = AssetManifestContract.CurrentSchemaVersion,
        PackId = packId,
        Version = "1.0.0",
        PrivateUseOnly = true,
        Attribution = new AssetAttribution { Creator = "fixture" },
        Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
        {
            ["base"] = new AssetOutfit
            {
                Animations = AssetManifestContract.RequiredAnimationKeys.ToDictionary(
                    key => key,
                    key => Animation(key),
                    StringComparer.Ordinal),
            },
        },
    };

    private static AssetAnimation Animation(string key, List<AssetFrame>? frames = null) => new()
    {
        Frames = frames ?? [new AssetFrame { File = "pose.png", DurationMs = 100, Sha256 = new string('a', 64) }],
        Loop = AssetManifestContract.OneShotAnimationKeys.Contains(key) ? "once" : "loop",
        Anchor = new PixelPoint(16, 24),
        NominalSize = new PixelSize(32, 32),
        ReducedMotion = "pose.png",
        ReducedMotionSha256 = new string('a', 64),
    };
}
