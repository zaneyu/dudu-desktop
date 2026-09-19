using System.Text.Json.Serialization;
using Dudu.Core.Models;

namespace Dudu.Core.Assets;

public static class AssetManifestContract
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Maximum frames in a single animation; bounds decode + compose work per frame tick.</summary>
    public const int MaxFramesPerAnimation = 64;

    /// <summary>Upper bound for a single frame duration; animations are ambient loops, not video.</summary>
    public const int MaxFrameDurationMs = 10_000;

    /// <summary>Upper bound for the summed duration of one animation loop.</summary>
    public const int MaxAnimationDurationMs = 10_000;

    /// <summary>Maximum decoded bytes for a single PNG file referenced by a pack.</summary>
    public const long MaxFileBytes = 2_097_152;

    /// <summary>Maximum summed bytes of every PNG file referenced by a pack.</summary>
    public const long MaxPackBytes = 33_554_432;

    public const string StickerAnimationPrefix = "sticker-";
    public const int StickerAnimationCount = 30;

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

    public static string StickerAnimationKey(int oneBasedIndex)
    {
        if (oneBasedIndex is < 1 or > StickerAnimationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));
        }

        return $"{StickerAnimationPrefix}{oneBasedIndex:000}";
    }

    public static bool IsStickerAnimationKey(string? key)
    {
        if (key is null
            || !key.StartsWith(StickerAnimationPrefix, StringComparison.Ordinal)
            || key.Length != StickerAnimationPrefix.Length + 3
            || !int.TryParse(key.AsSpan(StickerAnimationPrefix.Length), out var index))
        {
            return false;
        }

        return index is >= 1 and <= StickerAnimationCount
            && string.Equals(key, StickerAnimationKey(index), StringComparison.Ordinal);
    }

    public static bool IsSupportedAnimationKey(string? key) =>
        key is not null
        && (RequiredAnimationKeys.Contains(key, StringComparer.Ordinal) || IsStickerAnimationKey(key));

    public static bool IsOneShotAnimationKey(string? key) =>
        key is not null
        && (OneShotAnimationKeys.Contains(key) || IsStickerAnimationKey(key));

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
        else if (!IsSafeKey(manifest.PackId))
        {
            errors.Add("packId must be a safe identifier (letters, digits, '-', '_', '.').");
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
                if (!IsSupportedAnimationKey(animationKey))
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
                    if (!IsSupportedAnimationKey(animationKey))
                    {
                        errors.Add($"outfits.base.animations contains unsupported animation key '{animationKey}'.");
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

        // Span-based on purpose: the frame composer validates every frame it draws,
        // and Split/Replace here cost an allocation per animation frame.
        ReadOnlySpan<char> remaining = path;
        if (remaining[0] is '/' or '\\')
        {
            return false;
        }

        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOfAny('/', '\\');
            var segment = separator < 0 ? remaining : remaining[..separator];
            if (segment.SequenceEqual(".."))
            {
                return false;
            }

            remaining = separator < 0 ? [] : remaining[(separator + 1)..];
        }

        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
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
        else if (animation.Frames.Count > MaxFramesPerAnimation)
        {
            errors.Add($"{path}.frames must contain at most {MaxFramesPerAnimation} frames.");
        }
        else
        {
            var totalDurationMs = 0L;
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
                else if (frame.DurationMs > MaxFrameDurationMs)
                {
                    errors.Add($"{path}.frames[{index}].durationMs must not exceed {MaxFrameDurationMs}ms.");
                }
                else
                {
                    totalDurationMs += frame.DurationMs;
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

            if (totalDurationMs > MaxAnimationDurationMs)
            {
                errors.Add($"{path} total duration must not exceed {MaxAnimationDurationMs}ms.");
            }
        }

        if (!string.Equals(animation.Loop, "loop", StringComparison.Ordinal)
            && !string.Equals(animation.Loop, "once", StringComparison.Ordinal)
            && !string.Equals(animation.Loop, "hold", StringComparison.Ordinal))
        {
            errors.Add($"{path}.loop must be one of loop, once, or hold.");
        }

        var animationKey = path[(path.LastIndexOf('.') + 1)..];
        if (IsOneShotAnimationKey(animationKey) && string.Equals(animation.Loop, "loop", StringComparison.Ordinal))
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

        if (Manifest.Outfits.TryGetValue("base", out var baseOutfit)
            && baseOutfit.Animations.TryGetValue(animationKey, out var baseAnimation))
        {
            return baseAnimation;
        }

        if (Manifest.Outfits.TryGetValue(selectedOutfit, out outfit)
            && outfit.Animations.TryGetValue("idle", out var outfitIdle))
        {
            return outfitIdle;
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
