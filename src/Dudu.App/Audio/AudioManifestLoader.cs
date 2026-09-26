using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dudu.App.Audio;

public static class AudioManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<AudioCatalog> LoadAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
            throw new AudioManifestException("Manifest does not exist.");

        AudioManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(fullManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<AudioManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new AudioManifestException("Manifest JSON is invalid.", exception);
        }
        catch (IOException exception)
        {
            throw new AudioManifestException("Manifest could not be read.", exception);
        }

        var errors = AudioManifestContract.Validate(manifest).ToList();
        var waves = new Dictionary<AudioCueManifest, byte[]>(ReferenceEqualityComparer.Instance);
        if (manifest is not null && errors.Count == 0)
            await ValidateCueFilesAsync(fullManifestPath, manifest, errors, waves, cancellationToken);

        if (errors.Count > 0)
            throw new AudioManifestException("Audio manifest validation failed: " + string.Join("; ", errors));

        return new AudioCatalog(manifest!.Packs.Select(pack => new AudioSoundPack(
            pack.PackId,
            pack.Cues.Select(cue => new AudioCue(cue.FilePath, cue.DurationMs, cue.Sha256)
            {
                WaveData = waves[cue],
                PeakLevel = WavPcm.PeakLevel(waves[cue]),
            }).ToArray())).ToArray());
    }

    private static async Task ValidateCueFilesAsync(
        string manifestPath,
        AudioManifest manifest,
        ICollection<string> errors,
        IDictionary<AudioCueManifest, byte[]> waves,
        CancellationToken cancellationToken)
    {
        var root = Path.GetDirectoryName(manifestPath)!;
        foreach (var pack in manifest.Packs)
        {
            long packBytes = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cue in pack.Cues)
            {
                if (!AudioManifestContract.IsSafeRelativeWavePath(cue.FilePath))
                    continue;

                var fullPath = Path.GetFullPath(Path.Combine(root, cue.FilePath));
                if (!IsContained(root, fullPath) || !IsReparseSafe(root, fullPath))
                {
                    errors.Add($"cue '{cue.CueId}' path escape.");
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    errors.Add($"cue '{cue.CueId}' missing file.");
                    continue;
                }

                long fileBytes;
                try { fileBytes = new FileInfo(fullPath).Length; }
                catch (IOException) { errors.Add($"cue '{cue.CueId}' file size could not be read."); continue; }
                if (fileBytes > AudioManifestContract.MaxCueFileBytes)
                {
                    errors.Add($"cue '{cue.CueId}' large file exceeds {AudioManifestContract.MaxCueFileBytes} bytes.");
                    continue;
                }

                if (seenPaths.Add(fullPath))
                    packBytes += fileBytes;
                if (packBytes > AudioManifestContract.MaxPackBytes)
                {
                    errors.Add($"pack size exceeds {AudioManifestContract.MaxPackBytes} bytes.");
                    return;
                }

                try
                {
                    await using var stream = File.OpenRead(fullPath);
                    var bytes = new byte[fileBytes];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    if (!TryReadWave(bytes, out var actualDurationMs, out var waveError))
                    {
                        errors.Add($"cue '{cue.CueId}' {waveError}.");
                        continue;
                    }

                    if (Math.Abs(actualDurationMs - cue.DurationMs) > 10)
                        errors.Add($"cue '{cue.CueId}' duration disagreement.");

                    var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(actualHash, cue.Sha256, StringComparison.Ordinal))
                        errors.Add($"cue '{cue.CueId}' hash mismatch.");
                    else
                        waves[cue] = bytes;
                }
                catch (IOException)
                {
                    errors.Add($"cue '{cue.CueId}' file could not be read.");
                }
            }
        }
    }

    private static bool TryReadWave(byte[] bytes, out int durationMs, out string error)
    {
        durationMs = 0;
        error = "bad signature";
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return false;

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var riffEnd = 8L + riffSize;
        if (riffSize < 4 || riffEnd > bytes.Length || riffEnd != bytes.Length)
        {
            error = "has invalid RIFF boundary";
            return false;
        }

        var offset = 12;
        var foundFormat = false;
        var foundData = false;
        int byteRate = 0;
        int blockAlign = 0;
        int dataBytes = 0;
        while (offset < riffEnd)
        {
            if (riffEnd - offset < 8)
            {
                error = "has invalid RIFF chunk";
                return false;
            }
            var chunkId = bytes.AsSpan(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            var paddedChunkLength = 8L + chunkSize + (chunkSize & 1);
            if (chunkSize < 0 || paddedChunkLength > riffEnd - offset)
            {
                error = "has invalid RIFF chunk";
                return false;
            }
            var chunk = bytes.AsSpan(offset + 8, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    error = "has an invalid fmt chunk";
                    return false;
                }
                var format = BinaryPrimitives.ReadInt16LittleEndian(chunk);
                var channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]);
                var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[8..]);
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(chunk[12..]);
                var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
                var expectedBlockAlign = channels * (bitsPerSample / 8);
                var expectedByteRate = (long)sampleRate * expectedBlockAlign;
                if (format != 1 || channels is < 1 or > 2 || sampleRate <= 0 || bitsPerSample != 16 ||
                    blockAlign != expectedBlockAlign || byteRate != expectedByteRate)
                {
                    error = "non-PCM format";
                    return false;
                }
                foundFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataBytes = chunk.Length;
                foundData = true;
            }

            offset += (int)paddedChunkLength;
        }

        if (offset != riffEnd)
        {
                error = "has invalid RIFF boundary";
            return false;
        }

        if (!foundFormat || !foundData || byteRate <= 0 || dataBytes <= 0)
        {
            error = "has missing WAV chunks";
            return false;
        }

        if (blockAlign <= 0 || dataBytes % blockAlign != 0)
        {
            error = "has unaligned PCM data";
            return false;
        }

        durationMs = checked((int)Math.Round(dataBytes * 1000d / byteRate, MidpointRounding.AwayFromZero));
        if (durationMs <= 0)
        {
            error = "has an empty duration";
            return false;
        }
        return true;
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsReparseSafe(string root, string path)
    {
        if (HasReparsePoint(root)) return false;
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && HasReparsePoint(current)) return false;
        }
        return true;
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
