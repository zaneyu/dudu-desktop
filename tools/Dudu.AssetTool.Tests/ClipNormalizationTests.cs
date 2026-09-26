using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dudu.AssetTool;
using Dudu.Core.Assets;
using SkiaSharp;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class ClipNormalizationTests
{
    [Fact]
    public void SelectFrames_samples_every_nth_frame_and_keeps_the_source_timing()
    {
        var frames = Enumerable.Range(0, 7)
            .Select(index => new AssetInputFrame($"f{index}", Png(8, 8, 0, 0, 8, 8), 90))
            .ToArray();

        var selected = AssetNormalizer.SelectFrames(frames, startFrame: 1, frameCount: 5, frameStep: 2);

        Assert.Equal(["f1", "f3", "f5"], selected.Select(frame => frame.Name));
        Assert.Equal([180, 180, 90], selected.Select(frame => frame.DurationMs));
        Assert.Equal(5 * 90, selected.Sum(frame => frame.DurationMs));
    }

    [Fact]
    public void NormalizeClip_upscales_the_union_to_the_target_height_with_one_shared_transform()
    {
        // Two 10x20 figures at different positions: the union is 30x20.
        var frames = new[]
        {
            new AssetInputFrame("left", Png(40, 40, 0, 10, 10, 20), 100),
            new AssetInputFrame("right", Png(40, 40, 20, 10, 10, 20), 100),
        };

        var result = AssetNormalizer.NormalizeClip(frames, new PixelPoint(256, 400), 512, targetHeight: 200);

        Assert.All(result.Frames, frame => Assert.Equal(10d, frame.Scale, precision: 6));
        using var first = SKBitmap.Decode(result.Frames[0].PngBytes);
        using var second = SKBitmap.Decode(result.Frames[1].PngBytes);
        var firstBounds = OpaqueBounds(first);
        var secondBounds = OpaqueBounds(second);
        Assert.Equal(200, firstBounds.Height, tolerance: 2);
        Assert.Equal(firstBounds.Bottom, secondBounds.Bottom, tolerance: 1);
        // The shared transform keeps the 20px source offset (x10) between the figures.
        Assert.Equal(200, secondBounds.Left - firstBounds.Left, tolerance: 2);
    }

    [Fact]
    public void NormalizeClip_recenter_keeps_a_wandering_character_in_place()
    {
        var frames = new[]
        {
            new AssetInputFrame("left", Png(40, 40, 0, 10, 10, 20), 100),
            new AssetInputFrame("right", Png(40, 40, 30, 10, 10, 20), 100),
        };

        var result = AssetNormalizer.NormalizeClip(frames, new PixelPoint(256, 400), 512, targetHeight: 200, recenterFrames: true);

        using var first = SKBitmap.Decode(result.Frames[0].PngBytes);
        using var second = SKBitmap.Decode(result.Frames[1].PngBytes);
        Assert.Equal(OpaqueBounds(first).MidX, OpaqueBounds(second).MidX, tolerance: 1);
        Assert.Equal(256, OpaqueBounds(first).MidX, tolerance: 2);
    }

    [Fact]
    public void DecodeFrames_erases_caption_rectangles_before_background_removal()
    {
        var png = Png(20, 20, 0, 0, 20, 20);

        var frames = AssetNormalizer.DecodeFrames(png, "caption", 100, [new ClipRect(0, 0, 5, 6)]);

        using var decoded = SKBitmap.Decode(frames[0].Bytes);
        Assert.Equal((byte)0, decoded.GetPixel(4, 5).Alpha);
        Assert.Equal((byte)255, decoded.GetPixel(5, 5).Alpha);
        Assert.Equal((byte)255, decoded.GetPixel(4, 6).Alpha);
    }

    [Fact]
    public async Task Normalize_produces_optional_keys_with_one_shot_aware_loop_semantics()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root);

            var exitCode = await Program.Main(["normalize", "--sources", fixture.SourcesPath, "--input", fixture.RawDirectory, "--output", fixture.PackDirectory]);

            Assert.Equal(0, exitCode);
            var animations = ReadManifest(fixture.PackDirectory)["outfits"]!["base"]!["animations"]!.AsObject();
            Assert.Equal("loop", (string?)animations["drag"]!["loop"]);
            Assert.Equal("loop", (string?)animations["eat"]!["loop"]);
            Assert.Equal("once", (string?)animations["tantrum"]!["loop"]);
            Assert.Equal("once", (string?)animations["petted"]!["loop"]);
            Assert.Equal("once", (string?)animations["drink"]!["loop"]);
            Assert.Equal("loop", (string?)animations["focus"]!["loop"]);
            Assert.Equal("loop", (string?)animations["idle"]!["loop"]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Normalize_keys_regenerates_only_the_listed_keys_and_preserves_the_rest_of_the_pack()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            Assert.Equal(0, await Program.Main(["normalize", "--sources", fixture.SourcesPath, "--input", fixture.RawDirectory, "--output", fixture.PackDirectory]));

            // Simulate a hand-curated animation that the tool itself never generates.
            var manifest = ReadManifest(fixture.PackDirectory);
            var animations = manifest["outfits"]!["base"]!["animations"]!.AsObject();
            var sticker = animations["idle"]!.DeepClone();
            sticker["loop"] = "once";
            animations["sticker-001"] = sticker;
            var idleFile = (string)animations["idle"]!["frames"]![0]!["file"]!;
            await File.WriteAllTextAsync(Path.Combine(fixture.PackDirectory, "manifest.json"), manifest.ToJsonString(), TestContext.Current.CancellationToken);

            var exitCode = await Program.Main(
            [
                "normalize",
                "--sources", fixture.SourcesPath,
                "--input", fixture.RawDirectory,
                "--output", fixture.PackDirectory,
                "--keys", "drag,tantrum",
                "--version", "9.9.9",
            ]);

            Assert.Equal(0, exitCode);
            var updated = ReadManifest(fixture.PackDirectory);
            var updatedAnimations = updated["outfits"]!["base"]!["animations"]!.AsObject();
            Assert.Equal("9.9.9", (string?)updated["version"]);
            Assert.True(updatedAnimations.ContainsKey("sticker-001"));
            Assert.True(File.Exists(Path.Combine(fixture.PackDirectory, idleFile)));
            Assert.Equal("once", (string?)updatedAnimations["tantrum"]!["loop"]);
            Assert.Contains("https://example.test/optional", updated["attribution"]!["sourceUrls"]!.AsArray().Select(url => (string?)url));
            Assert.Equal(0, await Program.Main(["validate", Path.Combine(fixture.PackDirectory, "manifest.json")]));

            var sources = JsonNode.Parse(await File.ReadAllTextAsync(fixture.SourcesPath, TestContext.Current.CancellationToken))!;
            var optional = sources["sources"]!.AsArray().Single(source => (string?)source!["id"] == "optional")!;
            var outputs = optional["transform"]!["outputFiles"]!.AsArray().Select(file => (string)file!).ToArray();
            Assert.All(outputs, file => Assert.True(File.Exists(Path.Combine(fixture.PackDirectory, file)), file));
            Assert.Equal(outputs.Length, outputs.Distinct().Count());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Normalize_keys_rejects_keys_outside_the_required_and_optional_sets()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            Assert.Equal(0, await Program.Main(["normalize", "--sources", fixture.SourcesPath, "--input", fixture.RawDirectory, "--output", fixture.PackDirectory]));

            var exitCode = await Program.Main(["normalize", "--sources", fixture.SourcesPath, "--input", fixture.RawDirectory, "--output", fixture.PackDirectory, "--keys", "sticker-001"]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<(string SourcesPath, string RawDirectory, string PackDirectory)> CreateFixtureAsync(string root)
    {
        var rawDirectory = Path.Combine(root, "raw");
        Directory.CreateDirectory(rawDirectory);
        var required = AssetNormalizer.CreateNeutralBearPng();
        var optional = Png(64, 64, 8, 8, 40, 48);
        await File.WriteAllBytesAsync(Path.Combine(rawDirectory, "required.png"), required, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(rawDirectory, "optional.png"), optional, TestContext.Current.CancellationToken);
        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["privateUseOnly"] = true,
            ["accessedUtc"] = "2026-09-11T00:00:00Z",
            ["creator"] = "fixture",
            ["rightsNote"] = "fixture",
            ["sources"] = new JsonArray
            {
                Source("required", "https://example.test/required", "required.png", required, AssetManifestContract.RequiredAnimationKeys, clip: null),
                Source("optional", "https://example.test/optional", "optional.png", optional, AssetManifestContract.OptionalAnimationKeys, new JsonObject
                {
                    ["startFrame"] = 0,
                    ["frameStep"] = 1,
                    ["targetHeight"] = 300,
                }),
            },
        };
        var sourcesPath = Path.Combine(root, "sources.json");
        await File.WriteAllTextAsync(sourcesPath, document.ToJsonString(), TestContext.Current.CancellationToken);
        return (sourcesPath, rawDirectory, Path.Combine(root, "pack"));
    }

    private static JsonObject Source(string id, string discoveryUrl, string rawFile, byte[] payload, IEnumerable<string> keys, JsonObject? clip)
    {
        var source = new JsonObject
        {
            ["id"] = id,
            ["discoveryUrl"] = discoveryUrl,
            ["creator"] = "fixture",
            ["privateUseOnly"] = true,
            ["accessedUtc"] = "2026-09-11T00:00:00Z",
            ["animationKeys"] = new JsonArray(keys.Select(key => (JsonNode)JsonValue.Create(key)!).ToArray()),
            ["status"] = "imported",
            ["mediaUrl"] = discoveryUrl + "/media.png",
            ["rawFile"] = rawFile,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        };
        if (clip is not null)
        {
            source["clip"] = clip;
        }

        return source;
    }

    private static JsonNode ReadManifest(string packDirectory) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(packDirectory, "manifest.json")))!;

    private static byte[] Png(int width, int height, int x, int y, int rectangleWidth, int rectangleHeight)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(150, 105, 72, 255), Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(new SKRect(x, y, x + rectangleWidth, y + rectangleHeight), paint);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data!.ToArray();
    }

    private static SKRectI OpaqueBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = 0, bottom = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < 128)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        return new SKRectI(left, top, right, bottom);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-clip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
