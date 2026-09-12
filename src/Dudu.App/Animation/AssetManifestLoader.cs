using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dudu.Core;
using Dudu.Core.Assets;

namespace Dudu.App.Animation;

public static class AssetManifestLoader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static async Task<AssetPack> LoadAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new AssetManifestException($"Manifest does not exist: {fullManifestPath}");
        }

        AssetManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(fullManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<AssetManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new AssetManifestException($"Manifest is not valid JSON: {fullManifestPath}", exception);
        }
        catch (IOException exception)
        {
            throw new AssetManifestException($"Manifest could not be read: {fullManifestPath}", exception);
        }

        var errors = AssetManifestContract.Validate(manifest).ToList();
        if (manifest is not null && manifest.SchemaVersion != ProductInfo.ProtocolVersion)
        {
            errors.Add($"schemaVersion {manifest.SchemaVersion} is incompatible with protocol version {ProductInfo.ProtocolVersion}.");
        }

        if (manifest is not null)
        {
            await ValidateFilesAsync(fullManifestPath, manifest, errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            throw new AssetManifestException(
                $"Asset manifest validation failed for '{fullManifestPath}':{Environment.NewLine}- "
                + string.Join(Environment.NewLine + "- ", errors));
        }

        return new AssetPack(fullManifestPath, manifest!);
    }

    private static async Task ValidateFilesAsync(
        string manifestPath,
        AssetManifest manifest,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var root = Path.GetDirectoryName(manifestPath)
            ?? throw new AssetManifestException("The manifest path has no root directory.");

        foreach (var (outfitKey, outfit) in manifest.Outfits.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (outfit is null)
            {
                continue;
            }

            foreach (var (animationKey, animation) in outfit.Animations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (animation is null)
                {
                    continue;
                }

                foreach (var frame in animation.Frames)
                {
                    if (frame is null || !AssetManifestContract.IsSafeRelativePath(frame.File))
                    {
                        continue;
                    }

                    await ValidatePngAsync(
                        root,
                        frame.File,
                        frame.Sha256,
                        $"outfits.{outfitKey}.animations.{animationKey}.frames",
                        errors,
                        cancellationToken);
                }

                if (AssetManifestContract.IsSafeRelativePath(animation.ReducedMotion))
                {
                    await ValidatePngAsync(
                        root,
                        animation.ReducedMotion!,
                        expectedSha256: null,
                        $"outfits.{outfitKey}.animations.{animationKey}.reducedMotion",
                        errors,
                        cancellationToken);
                }
            }
        }
    }

    private static async Task ValidatePngAsync(
        string root,
        string relativePath,
        string? expectedSha256,
        string field,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relativeToRoot = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativeToRoot)
            || relativeToRoot == ".."
            || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            errors.Add($"{field} resolves outside the manifest directory.");
            return;
        }

        if (!File.Exists(fullPath))
        {
            errors.Add($"{field} file does not exist: {relativePath}.");
            return;
        }

        try
        {
            await using var stream = File.OpenRead(fullPath);
            var header = new byte[24];
            var read = await stream.ReadAsync(header, cancellationToken);
            if (read != header.Length || !header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                errors.Add($"{field} is not a PNG file: {relativePath}.");
                return;
            }

            if (!header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                errors.Add($"{field} has no PNG IHDR header: {relativePath}.");
                return;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            if (width is 0 or > 512 || height is 0 or > 512)
            {
                errors.Add($"{field} dimensions must be at most 512x512: {relativePath} is {width}x{height}.");
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                stream.Position = 0;
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                var actual = Convert.ToHexString(hash).ToLowerInvariant();
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{field} SHA-256 mismatch for {relativePath}: expected {expectedSha256}, actual {actual}.");
                }
            }
        }
        catch (IOException exception)
        {
            errors.Add($"{field} could not be read: {relativePath} ({exception.Message}).");
        }
    }
}
