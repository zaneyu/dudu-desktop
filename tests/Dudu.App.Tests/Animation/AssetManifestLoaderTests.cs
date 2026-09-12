using System.Text.Json;
using Dudu.App.Animation;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class AssetManifestLoaderTests
{
    [Fact]
    public async Task Loader_rejects_animation_without_reduced_motion_pose()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "pose.png"), PngFixture.OnePixel);
            var manifest = new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture",
                Version = "1.0.0",
                Attribution = new AssetAttribution { Creator = "fixture" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit
                    {
                        Animations = AssetManifestContract.RequiredAnimationKeys.ToDictionary(
                            key => key,
                            key => new AssetAnimation
                            {
                                Frames = [new AssetFrame { File = "pose.png", DurationMs = 100 }],
                                Loop = "loop",
                                Anchor = new PixelPoint(0, 0),
                                NominalSize = new PixelSize(1, 1),
                                ReducedMotion = key == "comfort-hug" ? null : "pose.png",
                            },
                            StringComparer.Ordinal),
                    },
                },
            };
            var path = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<AssetManifestException>(
                () => AssetManifestLoader.LoadAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("reducedMotion", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static class PngFixture
    {
        public static readonly byte[] OnePixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    }
}
