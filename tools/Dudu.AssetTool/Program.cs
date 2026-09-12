using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dudu.Core;
using Dudu.Core.Assets;

namespace Dudu.AssetTool;

public static class Program
{
    private const string AccessDate = "2026-09-11T00:00:00Z";
    private const long MaximumMediaBytes = 25 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "import" => await ImportAsync(args[1..]),
                "normalize" => await NormalizeAsync(args[1..]),
                "validate" => await ValidateAsync(args[1..]),
                "generate-fallback" => await GenerateFallbackAsync(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception exception) when (exception is AssetManifestException or InvalidDataException or IOException or HttpRequestException or ArgumentException)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: Dudu.AssetTool import|normalize|validate|generate-fallback ...");
        return 2;
    }

    private static async Task<int> ImportAsync(string[] args)
    {
        var sourcesPath = RequiredOption(args, "--sources");
        var outputPath = RequiredOption(args, "--output");
        var recordChecksums = args.Contains("--record-checksums", StringComparer.Ordinal);
        var rawDirectory = EnsureRawDirectory(outputPath);
        var document = await ReadSourcesAsync(sourcesPath);
        Directory.CreateDirectory(rawDirectory);

        using var client = MediaDownloader.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Dudu.AssetTool/1.0 private-local-import");
        foreach (var source in document.Sources)
        {
            await ImportSourceAsync(client, source, rawDirectory, recordChecksums);
        }

        await WriteSourcesAsync(sourcesPath, document);
        var imported = document.Sources.Count(source => source.Status == "imported");
        var unavailable = document.Sources.Count - imported;
        Console.WriteLine($"Import complete: {imported} imported, {unavailable} unavailable or rejected. Source metadata updated at {sourcesPath}.");
        return unavailable == 0 ? 0 : 1;
    }

    private static async Task ImportSourceAsync(HttpClient client, SourceRecord source, string rawDirectory, bool recordChecksums)
    {
        if (!source.PrivateUseOnly)
        {
            source.Status = "rejected";
            source.Notes = "Rejected: source record must be privateUseOnly=true.";
            Console.WriteLine($"{source.Id}: rejected (privateUseOnly=false).");
            return;
        }

        if (!DateTimeOffset.TryParse(source.AccessedUtc, out _)
            || !string.Equals(source.AccessedUtc, AccessDate, StringComparison.Ordinal))
        {
            source.Status = "rejected";
            source.Notes = $"Rejected: accessedUtc must be {AccessDate}.";
            Console.WriteLine($"{source.Id}: rejected (unexpected access date).");
            return;
        }

        byte[]? payload = null;
        if (!string.IsNullOrWhiteSpace(source.RawFile))
        {
            var existingPath = SafeRawPath(rawDirectory, source.RawFile);
            if (File.Exists(existingPath))
            {
                payload = await File.ReadAllBytesAsync(existingPath);
                if (!IsSafeMedia(payload))
                {
                    payload = null;
                    source.Notes = "Rejected existing raw file: it is not a supported image payload.";
                }
                else if (!string.IsNullOrWhiteSpace(source.Sha256)
                    && !HashMatches(payload, source.Sha256))
                {
                    payload = null;
                    source.Notes = "Rejected existing raw file: recorded SHA-256 does not match.";
                }
            }
        }

        if (payload is null)
        {
            try
            {
                var directMedia = await MediaDownloader.DownloadAsync(client, source.MediaUrl!, MaximumMediaBytes);
                if (!IsSafeMedia(directMedia.Bytes))
                {
                    source.Status = "rejected-media";
                    source.Notes = "Rejected media URL: payload was not a supported PNG, JPEG, GIF, or WebP image.";
                    Console.WriteLine($"{source.Id}: rejected-media (unsupported payload).");
                    return;
                }
                payload = directMedia.Bytes;
                source.MediaUrl = directMedia.FinalUri.AbsoluteUri;
            }
            catch (HttpRequestException exception)
            {
                source.Status = "unavailable";
                source.Notes = $"Explicit media URL could not be fetched safely: {exception.Message}";
                Console.WriteLine($"{source.Id}: unavailable ({exception.Message}).");
                return;
            }
            catch (InvalidDataException exception)
            {
                source.Status = "rejected-media";
                source.Notes = exception.Message;
                Console.WriteLine($"{source.Id}: rejected-media ({exception.Message}).");
                return;
            }
        }

        if (payload is null)
        {
            source.Status = "no-image-media";
            source.Notes = "No explicit accountable media URL yielded a supported image; no private asset was fabricated.";
            Console.WriteLine($"{source.Id}: no-image-media (explicit media URL yielded no supported image).");
            return;
        }

        var actualSha256 = Sha256(payload);
        if (!string.IsNullOrWhiteSpace(source.Sha256)
            && !HashMatches(payload, source.Sha256))
        {
            source.Status = "checksum-mismatch";
            source.Notes = $"Rejected media: SHA-256 changed from recorded {source.Sha256} to {actualSha256}.";
            Console.WriteLine($"{source.Id}: checksum-mismatch.");
            return;
        }

        var extension = MediaPayloadValidator.ExtensionFor(payload);
        var fileName = source.Id + extension;
        var outputPath = SafeRawPath(rawDirectory, fileName);
        await File.WriteAllBytesAsync(outputPath, payload);
        source.RawFile = fileName;
        source.Sha256 = actualSha256;
        source.Status = "imported";
        source.Notes = recordChecksums
            ? "Imported after media signature and payload-size checks; SHA-256 recorded."
            : "Imported after media signature and payload-size checks.";
        Console.WriteLine($"{source.Id}: imported {fileName} sha256={actualSha256}.");
    }

    private static async Task<int> NormalizeAsync(string[] args)
    {
        var sourcesPath = RequiredOption(args, "--sources");
        var inputPath = RequiredOption(args, "--input");
        var outputPath = RequiredOption(args, "--output");
        var document = await ReadSourcesAsync(sourcesPath);
        var rawDirectory = Path.GetFullPath(inputPath);
        var packDirectory = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(packDirectory);
        if (!IsReparseSafe(packDirectory, packDirectory))
        {
            throw new InvalidDataException($"Pack output directory contains a symlink, junction, or reparse point: {packDirectory}");
        }

        var framesDirectory = Path.Combine(packDirectory, "frames");
        if (Directory.Exists(framesDirectory))
        {
            if (!IsReparseSafe(packDirectory, framesDirectory))
            {
                throw new InvalidDataException($"Pack frame directory contains a symlink, junction, or reparse point: {framesDirectory}");
            }

            Directory.Delete(framesDirectory, recursive: true);
        }

        var missing = AssetManifestContract.RequiredAnimationKeys
            .Where(key => !document.Sources.Any(source => source.Status == "imported"
                && source.AnimationKeys.Contains(key, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(source.RawFile)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "Cannot normalize a private pack because no imported source media is recorded for animation key(s): "
                + string.Join(", ", missing)
                + ". The source pages did not provide enough image media; no private pack was fabricated.");
        }

        var animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal);
        var transformBySource = document.Sources.ToDictionary(source => source.Id, _ => new SourceTransform(), StringComparer.Ordinal);
        foreach (var animationKey in AssetManifestContract.RequiredAnimationKeys)
        {
            var source = document.Sources.First(candidate => candidate.Status == "imported"
                && candidate.AnimationKeys.Contains(animationKey, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(candidate.RawFile));
            var rawPath = SafeRawPath(rawDirectory, source.RawFile!);
            var bytes = await File.ReadAllBytesAsync(rawPath);
            if (!IsSafeMedia(bytes) || !HashMatches(bytes, source.Sha256))
            {
                throw new InvalidDataException($"Source {source.Id} failed SHA-256 or media validation before normalization.");
            }

            var decodedFrames = AssetNormalizer.DecodeFrames(bytes, source.Id, 180);
            var normalized = AssetNormalizer.Normalize(
                decodedFrames,
                new PixelPoint(256, 400),
                512);
            var transform = transformBySource[source.Id];
            var animationFrames = new List<AssetFrame>(normalized.Frames.Count);
            foreach (var (frame, index) in normalized.Frames.Select((frame, index) => (frame, index)))
            {
                var relativeFile = AssetNormalizer.DeterministicFileName(animationKey, index, frame.Sha256);
                var fullOutputPath = SafePackPath(packDirectory, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                await File.WriteAllBytesAsync(fullOutputPath, frame.PngBytes);
                transform.OutputFiles.Add(relativeFile);
                transform.OutputSha256.Add(frame.Sha256);
                transform.Normalizations.Add(new NormalizationDetails
                {
                    FrameIndex = index,
                    TrimmedSize = frame.TrimmedSize,
                    Scale = frame.Scale,
                    CanvasSize = frame.CanvasSize,
                    Anchor = frame.Anchor,
                    ColorFormat = frame.ColorFormat,
                });
                animationFrames.Add(new AssetFrame { File = relativeFile, DurationMs = frame.DurationMs, Sha256 = frame.Sha256 });
            }

            animations[animationKey] = new AssetAnimation
            {
                Frames = animationFrames,
                Loop = animationKey == "idle" || animationKey == "blink" || animationKey == "focus" ? "loop" : "once",
                Anchor = normalized.Anchor,
                NominalSize = new PixelSize(normalized.CanvasSize, normalized.CanvasSize),
                ReducedMotion = animationFrames[0].File,
                ReducedMotionSha256 = animationFrames[0].Sha256,
            };
        }

        var manifest = new AssetManifest
        {
            SchemaVersion = ProductInfo.ProtocolVersion,
            PackId = "private-dudu",
            Version = "1.0.0",
            PrivateUseOnly = true,
            Attribution = new AssetAttribution
            {
                Creator = document.Creator,
                SourceUrls = document.Sources.Select(source => source.DiscoveryUrl).ToList(),
                RightsNote = document.RightsNote,
            },
            Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
            {
                ["base"] = new AssetOutfit { DisplayName = "Private Dudu", Animations = animations },
            },
        };
        var errors = AssetManifestContract.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new AssetManifestException(string.Join(Environment.NewLine, errors));
        }

        await File.WriteAllTextAsync(
            Path.Combine(packDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        foreach (var source in document.Sources)
        {
            if (transformBySource.TryGetValue(source.Id, out var transform) && transform.OutputFiles.Count > 0)
            {
                source.Transform = transform;
            }
        }

        await WriteSourcesAsync(sourcesPath, document);
        Console.WriteLine($"Normalized private pack at {packDirectory}; all {animations.Count} required animation keys are backed by imported source media.");
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("validate requires exactly one manifest path.");
        }

        var manifestPath = Path.GetFullPath(args[0]);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Manifest does not exist.", manifestPath);
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<AssetManifest>(stream, JsonOptions);
        var errors = AssetManifestContract.Validate(manifest).ToList();
        if (manifest is not null)
        {
            var root = Path.GetDirectoryName(manifestPath)!;
            foreach (var outfit in manifest.Outfits.Values)
            {
                foreach (var animation in outfit.Animations.Values)
                {
                    foreach (var frame in animation.Frames)
                    {
                        await ValidateFileAsync(root, frame.File, frame.Sha256, errors);
                    }

                    await ValidateFileAsync(root, animation.ReducedMotion, animation.ReducedMotionSha256, errors);
                }
            }
        }

        if (errors.Count > 0)
        {
            Console.Error.WriteLine("Manifest invalid:");
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"- {error}");
            }

            return 1;
        }

        Console.WriteLine($"Manifest valid: {manifestPath}");
        return 0;
    }

    private static async Task<int> GenerateFallbackAsync(string[] args)
    {
        var outputPath = args.Length == 0 ? "src/Dudu.App/Assets/Packs/fallback" : RequiredOption(args, "--output");
        var directory = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(directory);
        var idlePath = Path.Combine(directory, "idle.png");
        var bytes = AssetNormalizer.CreateNeutralBearPng();
        await File.WriteAllBytesAsync(idlePath, bytes);
        var hash = Sha256(bytes);
        var animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal);
        foreach (var key in AssetManifestContract.RequiredAnimationKeys)
        {
            animations[key] = new AssetAnimation
            {
                Frames = [new AssetFrame { File = "idle.png", DurationMs = key == "idle" ? 1000 : 450, Sha256 = hash }],
                Loop = key == "idle" ? "loop" : "once",
                Anchor = new PixelPoint(64, 112),
                NominalSize = new PixelSize(128, 128),
                ReducedMotion = "idle.png",
                ReducedMotionSha256 = hash,
            };
        }

        var manifest = new AssetManifest
        {
            SchemaVersion = ProductInfo.ProtocolVersion,
            PackId = "fallback",
            Version = "1.0.0",
            PrivateUseOnly = false,
            Attribution = new AssetAttribution
            {
                Creator = "Dudu Desktop original fallback",
                RightsNote = "Original neutral bear silhouette generated for local fallback use; not copied from Dudu artwork.",
            },
            Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
            {
                ["base"] = new AssetOutfit { DisplayName = "Neutral bear", Animations = animations },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        Console.WriteLine($"Generated original fallback pack at {directory} sha256={hash}.");
        return 0;
    }

    private static async Task ValidateFileAsync(string root, string? relativePath, string? expectedSha256, ICollection<string> errors)
    {
        if (!AssetManifestContract.IsSafeRelativePath(relativePath))
        {
            return;
        }

        var path = SafePackPath(root, relativePath!);
        if (!File.Exists(path))
        {
            errors.Add($"Referenced PNG is missing: {relativePath}.");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path);
        if (!bytes.AsSpan().StartsWith(PngSignature))
        {
            errors.Add($"Referenced file is not PNG: {relativePath}.");
        }
        else if (bytes.Length < 26 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            errors.Add($"Referenced PNG has no valid IHDR header: {relativePath}.");
        }
        else
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is 0 or > 512 || height is 0 or > 512)
            {
                errors.Add($"Referenced PNG dimensions must be at most 512x512: {relativePath} is {width}x{height}.");
            }

            if (bytes[24] != 8 || bytes[25] != 6)
            {
                errors.Add($"Referenced PNG must be an 8-bit RGBA PNG: {relativePath}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) && !HashMatches(bytes, expectedSha256))
        {
            errors.Add($"Referenced PNG SHA-256 mismatch: {relativePath}.");
        }
    }

    private static async Task<SourceDocument> ReadSourcesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SourceDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Source metadata is empty: {path}");
        if (document.SchemaVersion != 1 || document.Sources.Count == 0)
        {
            throw new InvalidDataException($"Source metadata must be schemaVersion 1 with at least one source record: {path}");
        }

        foreach (var source in document.Sources)
        {
            MediaDownloader.RequireHttps(source.DiscoveryUrl);
            if (string.IsNullOrWhiteSpace(source.MediaUrl))
            {
                throw new InvalidDataException($"Source '{source.Id}' must record an explicit accountable mediaUrl; discovery-page scraping is disabled.");
            }

            MediaDownloader.RequireHttps(source.MediaUrl);
        }

        return document;
    }

    private static async Task WriteSourcesAsync(string path, SourceDocument document)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    private static string EnsureRawDirectory(string outputPath)
    {
        var raw = Path.GetFullPath(outputPath);
        var expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "assets", "raw"));
        if (!IsUnder(raw, expected) || !string.Equals(raw, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Downloads are only allowed directly under assets/raw: {expected}");
        }

        if (Directory.Exists(raw) && !IsReparseSafe(raw, raw))
        {
            throw new InvalidDataException($"Downloads cannot use a symlink, junction, or reparse point as assets/raw: {raw}");
        }

        return raw;
    }

    private static string SafeRawPath(string rawDirectory, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (fileName != Path.GetFileName(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal)
            || !new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe raw asset filename: {fileName}");
        }

        var path = Path.GetFullPath(Path.Combine(rawDirectory, fileName));
        if (!IsUnder(path, rawDirectory) || !IsReparseSafe(rawDirectory, path))
        {
            throw new InvalidDataException($"Raw asset path escapes assets/raw: {fileName}");
        }

        return path;
    }

    private static string SafePackPath(string packDirectory, string relativePath)
    {
        if (!AssetManifestContract.IsSafeRelativePath(relativePath))
        {
            throw new InvalidDataException($"Unsafe normalized asset path: {relativePath}");
        }

        var path = Path.GetFullPath(Path.Combine(packDirectory, relativePath));
        if (!IsUnder(path, packDirectory) || !IsReparseSafe(packDirectory, path))
        {
            throw new InvalidDataException($"Normalized asset path escapes the pack: {relativePath}");
        }

        return path;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparseSafe(string root, string path)
    {
        var rootFull = Path.GetFullPath(root);
        if (HasReparsePoint(rootFull))
        {
            return false;
        }

        var relative = Path.GetRelativePath(rootFull, Path.GetFullPath(path));
        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && HasReparsePoint(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsSafeMedia(byte[] bytes) => MediaPayloadValidator.IsSupportedImage(bytes, MaximumMediaBytes);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool HashMatches(byte[] bytes, string? expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && string.Equals(Sha256(bytes), expected, StringComparison.OrdinalIgnoreCase);

    private static string RequiredOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Missing required option {option}.");
        }

        return args[index + 1];
    }

    private sealed class SourceDocument
    {
        public int SchemaVersion { get; set; }
        public bool PrivateUseOnly { get; set; }
        public string AccessedUtc { get; set; } = AccessDate;
        public string Creator { get; set; } = string.Empty;
        public string RightsNote { get; set; } = string.Empty;
        public List<SourceRecord> Sources { get; set; } = [];
    }

    private sealed class SourceRecord
    {
        public string Id { get; set; } = string.Empty;
        public string DiscoveryUrl { get; set; } = string.Empty;
        public string Creator { get; set; } = string.Empty;
        public bool PrivateUseOnly { get; set; }
        public string AccessedUtc { get; set; } = AccessDate;
        public List<string> AnimationKeys { get; set; } = [];
        public string Status { get; set; } = "pending";
        public string? MediaUrl { get; set; }
        public string? RawFile { get; set; }
        public string? Sha256 { get; set; }
        public SourceTransform? Transform { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class SourceTransform
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public NormalizationDetails? Normalization { get; set; }
        public List<string> OutputFiles { get; set; } = [];
        public List<string> OutputSha256 { get; set; } = [];
        public List<NormalizationDetails> Normalizations { get; set; } = [];
    }

    private sealed class NormalizationDetails
    {
        public int FrameIndex { get; set; }
        public PixelSize TrimmedSize { get; set; }
        public double Scale { get; set; }
        public int CanvasSize { get; set; }
        public PixelPoint Anchor { get; set; }
        public string ColorFormat { get; set; } = string.Empty;
    }
}
