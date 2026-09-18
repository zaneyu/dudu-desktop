using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class AudioManifestLoaderTests
{
    [Fact]
    public async Task Loads_all_five_private_pack_ids()
    {
        await using var fixture = await AudioManifestFixture.CreateAsync(
            ["bubu-dudu-atata", "tata-lala", "dudu-lalala", "dudu-atatata", "dudu-yapapa"]);

        var catalog = await AudioManifestLoader.LoadAsync(
            fixture.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.PackIds, catalog.PackIds);
        Assert.Equal("dudu-atatata/atatata-01.wav", catalog.Resolve("dudu-atatata").Cues[0].FilePath);
        Assert.Equal(100, catalog.Resolve("dudu-atatata").Cues[0].DurationMs);
        Assert.Equal(fixture.Sha256, catalog.Resolve("dudu-atatata").Cues[0].Sha256);
    }

    [Fact]
    public async Task Rejects_malformed_manifests_without_leaking_urls_or_media_bytes()
    {
        var cases = new (string Name, Func<AudioManifestFixture, Task> Mutate)[]
        {
            ("rooted path", fixture => fixture.SetCuePathAsync("/outside.wav")),
            ("traversal", fixture => fixture.SetCuePathAsync("../outside.wav")),
            ("extension", fixture => fixture.SetCuePathAsync("bubu-dudu-atata/cue.mp3")),
            ("missing file", fixture => fixture.DeleteCueAsync()),
            ("bad signature", fixture => fixture.ReplaceCueBytesAsync("not-a-wave"u8.ToArray())),
            ("non pcm", fixture => fixture.SetPcmFormatAsync(3)),
            ("long duration", fixture => fixture.SetCueDurationAsync(AudioManifestContract.MaxCueDurationMs + 1)),
            ("large file", fixture => fixture.ReplaceCueBytesAsync(new byte[AudioManifestContract.MaxCueFileBytes + 1])),
            ("hash mismatch", fixture => fixture.SetCueHashAsync(new string('a', 64))),
            ("duplicate pack ids", fixture => fixture.DuplicatePackAsync()),
            ("missing required pack", fixture => fixture.RemovePackAsync("tata-lala")),
            ("non private", fixture => fixture.SetPrivateUseOnlyAsync(false)),
        };

        foreach (var testCase in cases)
        {
            await using var fixture = await AudioManifestFixture.CreateAsync(
                ["bubu-dudu-atata", "tata-lala", "dudu-lalala", "dudu-atatata", "dudu-yapapa"]);
            await fixture.SetSourceUrlAsync("https://private.example/source?media=SECRET_MEDIA_BYTES");
            await testCase.Mutate(fixture);

            var exception = await Assert.ThrowsAsync<AudioManifestException>(() =>
                AudioManifestLoader.LoadAsync(fixture.ManifestPath, TestContext.Current.CancellationToken));

            Assert.Contains(testCase.Name, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private.example", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET_MEDIA_BYTES", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rejects_unknown_json_members_case_mismatches_and_duration_disagreement()
    {
        await using var fixture = await AudioManifestFixture.CreateAsync(
            ["bubu-dudu-atata", "tata-lala", "dudu-lalala", "dudu-atatata", "dudu-yapapa"]);
        await fixture.SetRawManifestAsync(fixture.ManifestJson.Replace(
            "\"schemaVersion\":1",
            "\"SchemaVersion\":1,\"unexpected\":true",
            StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<AudioManifestException>(() =>
            AudioManifestLoader.LoadAsync(fixture.ManifestPath, TestContext.Current.CancellationToken));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contract_rejects_invalid_identifiers_and_short_cues()
    {
        var manifest = new AudioManifest
        {
            SchemaVersion = AudioManifestContract.CurrentSchemaVersion,
            PrivateUseOnly = true,
            Packs = [new AudioSoundPackManifest
            {
                PackId = "bad/id",
                Cues = [new AudioCueManifest { CueId = "cue", FilePath = "cue.wav", DurationMs = 79, Sha256 = new string('0', 64) }],
            }],
        };

        var errors = AudioManifestContract.Validate(manifest);

        Assert.Contains(errors, error => error.Contains("packId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("duration", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AudioManifestFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly AudioManifest _manifest;
        private byte[] _cueBytes;

        private AudioManifestFixture(string root, AudioManifest manifest, byte[] cueBytes, string sha256)
        {
            _root = root;
            _manifest = manifest;
            _cueBytes = cueBytes;
            Sha256 = sha256;
            PackIds = manifest.Packs.Select(pack => pack.PackId).ToArray();
            ManifestPath = Path.Combine(root, "manifest.json");
            ManifestJson = string.Empty;
        }

        public string ManifestPath { get; }
        public IReadOnlyList<string> PackIds { get; }
        public string Sha256 { get; private set; }
        public string ManifestJson { get; private set; }

        public static async Task<AudioManifestFixture> CreateAsync(IReadOnlyList<string> packIds)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-audio-loader-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var cueBytes = CreateWave(100);
            var sha256 = Convert.ToHexString(SHA256.HashData(cueBytes)).ToLowerInvariant();
            var manifest = new AudioManifest
            {
                SchemaVersion = 1,
                PrivateUseOnly = true,
                Attribution = new AudioAttribution { Creator = "fixture", SourceUrls = ["https://private.example/source"] },
                Packs = packIds.Select(packId => new AudioSoundPackManifest
                {
                    PackId = packId,
                    Cues = [new AudioCueManifest
                    {
                        CueId = "cue-01",
                        FilePath = $"{packId}/" + (packId == "dudu-atatata" ? "atatata-01.wav" : "cue-01.wav"),
                        DurationMs = 100,
                        Sha256 = sha256,
                    }],
                }).ToList(),
            };
            var fixture = new AudioManifestFixture(root, manifest, cueBytes, sha256);
            await fixture.WriteAsync();
            return fixture;
        }

        public async Task SetCuePathAsync(string path) { _manifest.Packs[0].Cues[0].FilePath = path; await WriteAsync(); }
        public async Task DeleteCueAsync() { await WriteAsync(); File.Delete(Path.Combine(_root, _manifest.Packs[0].Cues[0].FilePath)); }
        public async Task ReplaceCueBytesAsync(byte[] bytes) { _cueBytes = bytes; await WriteAsync(); }
        public async Task SetPcmFormatAsync(short format) { _cueBytes = CreateWave(100, format); await WriteAsync(); }
        public async Task SetCueDurationAsync(int duration) { _manifest.Packs[0].Cues[0].DurationMs = duration; await WriteAsync(); }
        public async Task SetCueHashAsync(string hash) { _manifest.Packs[0].Cues[0].Sha256 = hash; await WriteAsync(); }
        public async Task DuplicatePackAsync() { _manifest.Packs.Add(_manifest.Packs[0]); await WriteAsync(); }
        public async Task RemovePackAsync(string packId) { _manifest.Packs.RemoveAll(pack => pack.PackId == packId); await WriteAsync(); }
        public async Task SetPrivateUseOnlyAsync(bool value) { _manifest.PrivateUseOnly = value; await WriteAsync(); }
        public async Task SetSourceUrlAsync(string url) { _manifest.Attribution!.SourceUrls = [url]; await WriteAsync(); }
        public async Task SetRawManifestAsync(string json) { ManifestJson = json; await File.WriteAllTextAsync(ManifestPath, json); }

        private async Task WriteAsync()
        {
            var cuePath = _manifest.Packs[0].Cues[0].FilePath;
            if (AudioManifestContract.IsSafeRelativePath(cuePath))
            {
                var fullPath = Path.Combine(_root, cuePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllBytesAsync(fullPath, _cueBytes);
            }

            Sha256 = Convert.ToHexString(SHA256.HashData(_cueBytes)).ToLowerInvariant();
            ManifestJson = JsonSerializer.Serialize(_manifest);
            await File.WriteAllTextAsync(ManifestPath, ManifestJson);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static byte[] CreateWave(int durationMs, short format = 1)
        {
            const int sampleRate = 8_000;
            const short channels = 1;
            const short bitsPerSample = 16;
            var dataLength = sampleRate * channels * (bitsPerSample / 8) * durationMs / 1000;
            var bytes = new byte[44 + dataLength];
            "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
            "WAVE"u8.CopyTo(bytes.AsSpan(8));
            "fmt "u8.CopyTo(bytes.AsSpan(12));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), format);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), channels);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * channels * bitsPerSample / 8);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), channels * bitsPerSample / 8);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), bitsPerSample);
            "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), dataLength);
            return bytes;
        }
    }
}
