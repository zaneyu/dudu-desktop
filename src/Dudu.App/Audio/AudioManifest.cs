namespace Dudu.App.Audio;

public sealed class AudioManifest
{
    public int SchemaVersion { get; set; }
    public bool PrivateUseOnly { get; set; }
    public AudioAttribution? Attribution { get; set; }
    public List<AudioSoundPackManifest> Packs { get; set; } = [];
}

public sealed class AudioAttribution
{
    public string Creator { get; set; } = string.Empty;
    public List<string> SourceUrls { get; set; } = [];
}

public sealed class AudioSoundPackManifest
{
    public string PackId { get; set; } = string.Empty;
    public List<AudioCueManifest> Cues { get; set; } = [];
}

public sealed class AudioCueManifest
{
    public string CueId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class AudioCatalog
{
    private readonly IReadOnlyDictionary<string, AudioSoundPack> _packs;

    public AudioCatalog(IReadOnlyList<AudioSoundPack> packs)
    {
        Packs = packs;
        PackIds = packs.Select(pack => pack.PackId).ToArray();
        _packs = packs.ToDictionary(pack => pack.PackId, StringComparer.Ordinal);
    }

    public IReadOnlyList<AudioSoundPack> Packs { get; }
    public IReadOnlyList<string> PackIds { get; }

    public AudioSoundPack Resolve(string packId) =>
        _packs.TryGetValue(packId, out var pack)
            ? pack
            : throw new KeyNotFoundException($"Audio pack '{packId}' was not found.");
}

public sealed record AudioSoundPack(string PackId, IReadOnlyList<AudioCue> Cues);

public sealed record AudioCue(string FilePath, int DurationMs, string Sha256)
{
    public string RelativeFile => FilePath;
}

public sealed class AudioManifestException : Exception
{
    public AudioManifestException(string message) : base(message) { }
    public AudioManifestException(string message, Exception innerException) : base(message, innerException) { }
}

public static class AudioManifestContract
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxCueDurationMs = 4_000;
    public const long MaxCueFileBytes = 1_048_576;
    public const long MaxPackBytes = 8_388_608;

    private static readonly string[] RequiredPackIds =
        ["bubu-dudu-atata", "tata-lala", "dudu-lalala", "dudu-atatata", "dudu-yapapa"];

    public static IReadOnlyList<string> Validate(AudioManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null) return ["manifest is missing."];
        if (manifest.SchemaVersion != CurrentSchemaVersion) errors.Add("schemaVersion is unsupported.");
        if (!manifest.PrivateUseOnly) errors.Add("privateUseOnly must be true.");
        if (manifest.Packs is null) return [.. errors, "packs is missing."];

        var packIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in manifest.Packs)
        {
            if (pack is null) { errors.Add("pack entry is null."); continue; }
            if (!IsSafeIdentifier(pack.PackId)) errors.Add($"packId '{pack.PackId}' is invalid.");
            if (!packIds.Add(pack.PackId)) errors.Add($"duplicate packId '{pack.PackId}'.");
            if (pack.Cues is null || pack.Cues.Count == 0) { errors.Add($"pack '{pack.PackId}' has no cues."); continue; }

            var cueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cue in pack.Cues)
            {
                if (cue is null) { errors.Add($"pack '{pack.PackId}' cue entry is null."); continue; }
                if (!IsSafeIdentifier(cue.CueId)) errors.Add($"cueId '{cue.CueId}' is invalid.");
                if (!cueIds.Add(cue.CueId)) errors.Add($"duplicate cueId '{cue.CueId}'.");
                if (Path.IsPathRooted(cue.FilePath)) errors.Add($"cue '{cue.CueId}' rooted path is invalid.");
                else if (cue.FilePath.Split(['/', '\\']).Any(segment => segment is "..")) errors.Add($"cue '{cue.CueId}' traversal path is invalid.");
                else if (!cue.FilePath.EndsWith(".wav", StringComparison.Ordinal)) errors.Add($"cue '{cue.CueId}' extension is invalid.");
                else if (!IsSafeRelativeWavePath(cue.FilePath)) errors.Add($"cue '{cue.CueId}' path is invalid.");
                if (cue.DurationMs is < 80 or > MaxCueDurationMs) errors.Add($"cue '{cue.CueId}' duration is invalid.");
                if (!IsLowercaseSha256(cue.Sha256)) errors.Add($"cue '{cue.CueId}' hash is invalid.");
            }
        }

        if (packIds.Count != RequiredPackIds.Length || !RequiredPackIds.All(packIds.Contains))
            errors.Add("pack set must contain exactly the five required packs.");
        foreach (var required in RequiredPackIds)
            if (!packIds.Contains(required)) errors.Add($"missing required pack '{required}'.");
        return errors;
    }

    public static bool IsSafeIdentifier(string value) =>
        !string.IsNullOrEmpty(value) && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');

    public static bool IsSafeRelativePath(string value) => IsSafeRelativeWavePath(value);

    public static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSafeRelativeWavePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || !value.EndsWith(".wav", StringComparison.Ordinal)) return false;
        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or "..")) return false;
        return segments[..^1].All(IsSafeIdentifier) && IsSafeIdentifier(segments[^1][..^4]);
    }
}
