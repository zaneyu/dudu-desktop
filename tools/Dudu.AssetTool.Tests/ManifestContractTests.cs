using System.Text.Json;
using System.Text.Json.Serialization;
using Dudu.AssetTool;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class ManifestContractTests
{
    [Fact]
    public void Schema_v1_requires_exact_base_animation_key_set()
    {
        var manifest = FixtureManifest.Create();
        Assert.Empty(AssetManifestContract.Validate(manifest));

        manifest.Outfits["base"].Animations.Remove("idle");
        manifest.Outfits["base"].Animations["unknown"] = FixtureManifest.Animation("unknown");

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("idle", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_requires_frame_and_reduced_motion_hashes()
    {
        var manifest = FixtureManifest.Create();
        manifest.Outfits["base"].Animations["comfort-hug"] = new AssetAnimation
        {
            Frames = [new AssetFrame { File = "pose.png", DurationMs = 10 }],
            Loop = "loop",
            Anchor = new PixelPoint(1, 1),
            NominalSize = new PixelSize(2, 2),
            ReducedMotion = "pose.png",
        };

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("frames[0].sha256", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("reducedMotionSha256", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must not be loop", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_rejects_non_positive_duration_traversal_and_bad_geometry()
    {
        var manifest = FixtureManifest.Create();
        manifest.Outfits["base"].Animations["idle"] = new AssetAnimation
        {
            Frames = [new AssetFrame { File = "../escape.png", DurationMs = 0, Sha256 = new string('a', 64) }],
            Loop = "loop",
            Anchor = new PixelPoint(33, -1),
            NominalSize = new PixelSize(513, 0),
            ReducedMotion = "../escape.png",
            ReducedMotionSha256 = new string('a', 64),
        };

        var errors = AssetManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("relative .png", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("durationMs must be positive", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("nominalSize", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("anchor", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("reducedMotion must", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_manifest_properties_are_rejected()
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var json = "{\"schemaVersion\":1,\"unknown\":true}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AssetManifest>(json, options));
    }

    [Fact]
    public async Task Validate_rejects_a_frame_hash_mismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var png = AssetNormalizer.CreateNeutralBearPng(32);
            await File.WriteAllBytesAsync(Path.Combine(root, "pose.png"), png, TestContext.Current.CancellationToken);
            var manifest = FixtureManifest.Create(Sha256(png));
            var path = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(path, json.Replace(Sha256(png), new string('0', 64), StringComparison.Ordinal), TestContext.Current.CancellationToken);

            var exitCode = await Program.Main(["validate", path]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_rejects_a_symlinked_frame_when_supported()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-manifest-symlink-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "dudu-manifest-symlink-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var png = AssetNormalizer.CreateNeutralBearPng(32);
            var outsidePath = Path.Combine(outside, "pose.png");
            await File.WriteAllBytesAsync(outsidePath, png, TestContext.Current.CancellationToken);
            var linkedPath = Path.Combine(root, "linked.png");
            try
            {
                File.CreateSymbolicLink(linkedPath, outsidePath);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Symbolic links are unavailable: {exception.Message}");
                return;
            }

            var manifest = FixtureManifest.Create(Sha256(png));
            foreach (var animation in manifest.Outfits["base"].Animations.Values)
            {
                animation.Frames[0] = new AssetFrame { File = "linked.png", DurationMs = animation.Frames[0].DurationMs, Sha256 = Sha256(png) };
            }

            var path = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);

            var exitCode = await Program.Main(["validate", path]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static class FixtureManifest
    {
        public static AssetManifest Create(string? sha256 = null)
        {
            sha256 ??= new string('a', 64);
            return new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture",
                Version = "1.0.0",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "fixture" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit
                    {
                        Animations = AssetManifestContract.RequiredAnimationKeys.ToDictionary(
                            key => key,
                            key => Animation(key, sha256),
                            StringComparer.Ordinal),
                    },
                },
            };
        }

        public static AssetAnimation Animation(string key, string sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") => new()
        {
            Frames = [new AssetFrame { File = "pose.png", DurationMs = 100, Sha256 = sha256 }],
            Loop = AssetManifestContract.OneShotAnimationKeys.Contains(key) ? "once" : "loop",
            Anchor = new PixelPoint(16, 24),
            NominalSize = new PixelSize(32, 32),
            ReducedMotion = "pose.png",
            ReducedMotionSha256 = sha256,
        };
    }
}
