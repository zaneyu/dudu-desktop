using System.Text.Json;
using System.Text.Json.Serialization;
using Dudu.Core.Assets;
using SkiaSharp;
using Xunit;

namespace Dudu.AssetTool.Tests;

/// <summary>
/// Regression for the pet overlay window taking real per-pixel mouse input
/// (OverlayWindowHost's WM_NCHITTEST returns HTTRANSPARENT only for
/// fully-transparent pixels). A shipped frame that is fully or almost fully
/// opaque becomes a dead click zone wherever it is drawn on the desktop, so
/// every PNG referenced by every shipped asset pack's manifest must have a
/// meaningfully transparent region. Runs on Mac: Dudu.AssetTool(.Tests)
/// targets plain net10.0 and already references SkiaSharp.
/// </summary>
public sealed class ShippedFrameAlphaTests
{
    private const double MinimumFullyTransparentPixelShare = 0.10;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IEnumerable<object[]> ShippedManifestPaths()
    {
        var packsRoot = Path.Combine(FindRepositoryRoot(), "src", "Dudu.App", "Assets", "Packs");
        foreach (var manifestPath in Directory
            .EnumerateFiles(packsRoot, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return [manifestPath];
        }
    }

    [Theory]
    [MemberData(nameof(ShippedManifestPaths))]
    public void Every_shipped_frame_has_a_meaningfully_transparent_region(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<AssetManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
        Assert.NotNull(manifest);

        var root = Path.GetDirectoryName(manifestPath)!;
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var (outfitKey, outfit) in manifest!.Outfits)
        {
            foreach (var (animationKey, animation) in outfit.Animations)
            {
                var relativePaths = animation.Frames
                    .Where(frame => frame is not null)
                    .Select(frame => frame!.File)
                    .Append(animation.ReducedMotion)
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .Select(file => file!);

                foreach (var relativePath in relativePaths)
                {
                    if (!checkedPaths.Add(relativePath))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                    if (!File.Exists(fullPath))
                    {
                        // Missing/mismatched file coverage belongs to AssetManifestLoader's own tests.
                        continue;
                    }

                    using var bitmap = SKBitmap.Decode(fullPath);
                    if (bitmap is null)
                    {
                        failures.Add($"{outfitKey}/{animationKey}: {relativePath} could not be decoded.");
                        continue;
                    }

                    var transparentShare = FullyTransparentPixelShare(bitmap);
                    if (transparentShare < MinimumFullyTransparentPixelShare)
                    {
                        failures.Add(
                            $"{outfitKey}/{animationKey}: {relativePath} is only {transparentShare:P1} fully "
                                + $"transparent (needs at least {MinimumFullyTransparentPixelShare:P0}); an opaque "
                                + "frame is a dead click zone now that the pet overlay window takes real input.");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static double FullyTransparentPixelShare(SKBitmap bitmap)
    {
        var total = (long)bitmap.Width * bitmap.Height;
        if (total == 0)
        {
            return 0;
        }

        long transparent = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    transparent++;
                }
            }
        }

        return (double)transparent / total;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
