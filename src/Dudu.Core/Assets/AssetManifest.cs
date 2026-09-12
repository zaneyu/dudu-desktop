using System.Text.Json.Serialization;
using Dudu.Core.Models;

namespace Dudu.Core.Assets;

public static class AssetManifestContract
{
    public const int CurrentSchemaVersion = 1;

    public static IReadOnlyList<string> RequiredAnimationKeys { get; } =
    [
        "idle",
        "blink",
        "greeting",
        "sleep",
        "drink",
        "focus",
        "celebrate",
        "comfort-hug",
        "note-arrival",
    ];

    public static IReadOnlySet<string> OneShotAnimationKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "greeting",
            "sleep",
            "drink",
            "celebrate",
            "comfort-hug",
            "note-arrival",
        };

    public static IReadOnlyList<string> Validate(AssetManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            errors.Add("Manifest is null.");
            return errors;
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"schemaVersion must be {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.PackId))
        {
            errors.Add("packId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("version is required.");
        }

        if (manifest.PrivateUseOnly is null)
        {
            errors.Add("privateUseOnly is required.");
        }

        if (manifest.Attribution is null || string.IsNullOrWhiteSpace(manifest.Attribution.Creator))
        {
            errors.Add("attribution.creator is required; do not use it to imply a license.");
        }

        if (manifest.Outfits is null || manifest.Outfits.Count == 0)
        {
            errors.Add("outfits must contain at least the base outfit.");
            return errors;
        }

        if (!manifest.Outfits.ContainsKey("base"))
        {
            errors.Add("outfits.base is required.");
        }

        foreach (var (outfitKey, outfit) in manifest.Outfits.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsSafeKey(outfitKey))
            {
                errors.Add($"outfits key '{outfitKey}' is not a safe identifier.");
            }

            if (outfit is null || outfit.Animations is null || outfit.Animations.Count == 0)
            {
                errors.Add($"outfits.{outfitKey}.animations must not be empty.");
                continue;
            }

            if (!string.Equals(outfitKey, "base", StringComparison.Ordinal)
                && !outfit.Animations.ContainsKey("idle"))
            {
                errors.Add($"outfits.{outfitKey}.animations.idle is required for outfit fallback.");
            }

            foreach (var (animationKey, animation) in outfit.Animations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!RequiredAnimationKeys.Contains(animationKey, StringComparer.Ordinal))
                {
                    errors.Add($"outfits.{outfitKey}.animations.{animationKey} is not a supported animation key.");
                }

                ValidateAnimation(errors, $"outfits.{outfitKey}.animations.{animationKey}", animation);
            }

            if (string.Equals(outfitKey, "base", StringComparison.Ordinal))
            {
                foreach (var requiredKey in RequiredAnimationKeys)
                {
                    if (!outfit.Animations.ContainsKey(requiredKey))
                    {
                        errors.Add($"outfits.base.animations.{requiredKey} is required.");
                    }
                }

                foreach (var animationKey in outfit.Animations.Keys)
                {
                    if (!RequiredAnimationKeys.Contains(animationKey, StringComparer.Ordinal))
                    {
                        errors.Add($"outfits.base.animations must contain exactly the required animation keys; '{animationKey}' is unexpected.");
                    }
                }
            }
        }

        return errors;
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return false;
        }

        return normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void ValidateAnimation(
        ICollection<string> errors,
        string path,
        AssetAnimation? animation)
    {
        if (animation is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (animation.Frames is null || animation.Frames.Count == 0)
        {
            errors.Add($"{path}.frames must contain at least one frame.");
        }
        else
        {
            for (var index = 0; index < animation.Frames.Count; index++)
            {
                var frame = animation.Frames[index];
                if (frame is null)
                {
                    errors.Add($"{path}.frames[{index}] is null.");
                    continue;
                }

                if (!IsSafeRelativePath(frame.File))
                {
                    errors.Add($"{path}.frames[{index}].file must be a relative .png path.");
                }

                if (frame.DurationMs <= 0)
                {
                    errors.Add($"{path}.frames[{index}].durationMs must be positive.");
                }

                if (string.IsNullOrWhiteSpace(frame.Sha256))
                {
                    errors.Add($"{path}.frames[{index}].sha256 is required.");
                }
                else if (!IsSha256(frame.Sha256))
                {
                    errors.Add($"{path}.frames[{index}].sha256 must be a 64-character hexadecimal SHA-256.");
                }
            }
        }

        if (!string.Equals(animation.Loop, "loop", StringComparison.Ordinal)
            && !string.Equals(animation.Loop, "once", StringComparison.Ordinal)
            && !string.Equals(animation.Loop, "hold", StringComparison.Ordinal))
        {
            errors.Add($"{path}.loop must be one of loop, once, or hold.");
        }

        var animationKey = path[(path.LastIndexOf('.') + 1)..];
        if (OneShotAnimationKeys.Contains(animationKey) && string.Equals(animation.Loop, "loop", StringComparison.Ordinal))
        {
            errors.Add($"{path}.loop must not be loop for a one-shot animation.");
        }

        if (animation.NominalSize.Width <= 0 || animation.NominalSize.Height <= 0)
        {
            errors.Add($"{path}.nominalSize must be positive.");
        }
        else if (animation.NominalSize.Width > 512 || animation.NominalSize.Height > 512)
        {
            errors.Add($"{path}.nominalSize must not exceed 512x512.");
        }

        if (animation.Anchor.X < 0 || animation.Anchor.Y < 0
            || animation.Anchor.X >= animation.NominalSize.Width
            || animation.Anchor.Y >= animation.NominalSize.Height)
        {
            errors.Add($"{path}.anchor must be inside nominalSize.");
        }

        if (!IsSafeRelativePath(animation.ReducedMotion))
        {
            errors.Add($"{path}.reducedMotion must reference a relative .png pose.");
        }

        if (!IsSha256(animation.ReducedMotionSha256))
        {
            errors.Add($"{path}.reducedMotionSha256 is required and must be a 64-character hexadecimal SHA-256.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed class AssetManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("packId")]
    public string PackId { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("privateUseOnly")]
    public bool? PrivateUseOnly { get; init; }

    [JsonPropertyName("attribution")]
    public AssetAttribution Attribution { get; init; } = new();

    [JsonPropertyName("outfits")]
    public Dictionary<string, AssetOutfit> Outfits { get; init; } = new(StringComparer.Ordinal);
}

public sealed class AssetAttribution
{
    [JsonPropertyName("creator")]
    public string Creator { get; init; } = string.Empty;

    [JsonPropertyName("sourceUrls")]
    public List<string> SourceUrls { get; init; } = [];

    [JsonPropertyName("rightsNote")]
    public string? RightsNote { get; init; }
}

public sealed class AssetOutfit
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("animations")]
    public Dictionary<string, AssetAnimation> Animations { get; init; } = new(StringComparer.Ordinal);
}

public sealed class AssetAnimation
{
    [JsonPropertyName("frames")]
    public List<AssetFrame> Frames { get; init; } = [];

    [JsonPropertyName("loop")]
    public string Loop { get; init; } = "loop";

    [JsonPropertyName("anchor")]
    public PixelPoint Anchor { get; init; }

    [JsonPropertyName("nominalSize")]
    public PixelSize NominalSize { get; init; }

    [JsonPropertyName("reducedMotion")]
    public string? ReducedMotion { get; init; }

    [JsonPropertyName("reducedMotionSha256")]
    public string? ReducedMotionSha256 { get; init; }
}

public sealed class AssetFrame
{
    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}

public readonly record struct PixelPoint(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

public readonly record struct PixelSize(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed class AssetPack
{
    public AssetPack(string manifestPath, AssetManifest manifest)
    {
        ManifestPath = manifestPath;
        Manifest = manifest;
    }

    public string ManifestPath { get; }

    public string RootDirectory => Path.GetDirectoryName(ManifestPath)
        ?? throw new InvalidOperationException("The manifest path has no directory.");

    public AssetManifest Manifest { get; }

    public string ResolveOutfit(
        DateOnly localDate,
        SeasonalDates dates,
        string? manualOutfit = null) =>
        SeasonalOutfitPolicy.Select(localDate, dates, Manifest.Outfits.Keys, manualOutfit);

    public AssetAnimation ResolveAnimation(
        string animationKey,
        DateOnly localDate,
        SeasonalDates dates,
        string? manualOutfit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationKey);
        ArgumentNullException.ThrowIfNull(dates);

        var selectedOutfit = ResolveOutfit(localDate, dates, manualOutfit);
        if (Manifest.Outfits.TryGetValue(selectedOutfit, out var outfit)
            && outfit.Animations.TryGetValue(animationKey, out var selectedAnimation))
        {
            return selectedAnimation;
        }

        if (Manifest.Outfits.TryGetValue(selectedOutfit, out outfit)
            && outfit.Animations.TryGetValue("idle", out var outfitIdle))
        {
            return outfitIdle;
        }

        if (Manifest.Outfits.TryGetValue("base", out var baseOutfit)
            && baseOutfit.Animations.TryGetValue(animationKey, out var baseAnimation))
        {
            return baseAnimation;
        }

        if (Manifest.Outfits.TryGetValue("base", out baseOutfit)
            && baseOutfit.Animations.TryGetValue("idle", out var baseIdle))
        {
            return baseIdle;
        }

        throw new AssetManifestException("The manifest has no usable base idle animation.");
    }
}

public sealed class AssetManifestException : Exception
{
    public AssetManifestException(string message)
        : base(message)
    {
    }

    public AssetManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
