using System.Security.Cryptography;
using System.Text.Json;
using Dudu.AssetTool;
using Dudu.Core.Assets;
using Xunit;

namespace Dudu.AssetTool.Tests;

public sealed class PackOutputContainmentTests
{
    [Fact]
    public async Task Normalize_rejects_symlinked_output_parent_without_touching_external_target()
    {
        var root = CreateRoot();
        var external = Path.Combine(root, "external");
        var linkedParent = Path.Combine(root, "linked-output");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "sentinel.txt"), "untouched", TestContext.Current.CancellationToken);
        if (!TryCreateDirectoryLink(linkedParent, external))
        {
            Cleanup(root);
            Assert.Skip("Symbolic directories are unavailable on this platform.");
            return;
        }

        try
        {
            var fixture = await CreateImportedSourceAsync(root);
            var exitCode = await Program.Main(
            [
                "normalize",
                "--sources", fixture.SourcesPath,
                "--input", fixture.RawDirectory,
                "--output", Path.Combine(linkedParent, "pack"),
            ]);

            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(Path.Combine(external, "pack")));
            Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(external, "sentinel.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Normalize_rejects_symlinked_manifest_without_overwriting_external_target()
    {
        var root = CreateRoot();
        var output = Path.Combine(root, "pack");
        var external = Path.Combine(root, "external-manifest.json");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(external, "manifest sentinel", TestContext.Current.CancellationToken);
        var manifestPath = Path.Combine(output, "manifest.json");
        if (!TryCreateFileLink(manifestPath, external))
        {
            Cleanup(root);
            Assert.Skip("Symbolic files are unavailable on this platform.");
            return;
        }

        try
        {
            var fixture = await CreateImportedSourceAsync(root);
            var exitCode = await Program.Main(
            [
                "normalize",
                "--sources", fixture.SourcesPath,
                "--input", fixture.RawDirectory,
                "--output", output,
            ]);

            Assert.Equal(1, exitCode);
            Assert.Equal("manifest sentinel", await File.ReadAllTextAsync(external, TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task GenerateFallback_rejects_symlinked_destination_without_overwriting_external_target()
    {
        var root = CreateRoot();
        var output = Path.Combine(root, "fallback");
        var external = Path.Combine(root, "external-idle.png");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(external, "idle sentinel", TestContext.Current.CancellationToken);
        if (!TryCreateFileLink(Path.Combine(output, "idle.png"), external))
        {
            Cleanup(root);
            Assert.Skip("Symbolic files are unavailable on this platform.");
            return;
        }

        try
        {
            var exitCode = await Program.Main(["generate-fallback", "--output", output]);

            Assert.Equal(1, exitCode);
            Assert.Equal("idle sentinel", await File.ReadAllTextAsync(external, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task GenerateFallback_writes_a_safe_normal_directory()
    {
        var root = CreateRoot();
        var output = Path.Combine(root, "fallback");
        try
        {
            var exitCode = await Program.Main(["generate-fallback", "--output", output]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, "idle.png")));
            Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<(string SourcesPath, string RawDirectory)> CreateImportedSourceAsync(string root)
    {
        var rawDirectory = Path.Combine(root, "raw");
        Directory.CreateDirectory(rawDirectory);
        var payload = AssetNormalizer.CreateNeutralBearPng();
        await File.WriteAllBytesAsync(Path.Combine(rawDirectory, "fixture.png"), payload, TestContext.Current.CancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var sourcesPath = Path.Combine(root, "sources.json");
        var source = new
        {
            schemaVersion = 1,
            privateUseOnly = true,
            accessedUtc = "2026-09-11T00:00:00Z",
            creator = "fixture",
            rightsNote = "fixture",
            sources = new[]
            {
                new
                {
                    id = "fixture",
                    discoveryUrl = "https://example.test/discovery",
                    creator = "fixture",
                    privateUseOnly = true,
                    accessedUtc = "2026-09-11T00:00:00Z",
                    animationKeys = AssetManifestContract.RequiredAnimationKeys,
                    status = "imported",
                    mediaUrl = "https://example.test/media.png",
                    rawFile = "fixture.png",
                    sha256,
                },
            },
        };
        await File.WriteAllTextAsync(sourcesPath, JsonSerializer.Serialize(source), TestContext.Current.CancellationToken);
        return (sourcesPath, rawDirectory);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-pack-output-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
