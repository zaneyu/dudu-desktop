using Dudu.App.Hosting;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Exercises the WinUI-free <c>--self-test</c> core logic directly (no WinUI,
/// no installed app, no real Windows host required) against a temporary
/// <see cref="AppPaths"/> root, matching how App.xaml.cs's <c>OnLaunched</c>
/// drives <see cref="SelfTestRunner.RunAsync"/> when <c>--self-test</c> is
/// passed on the command line.
/// </summary>
public sealed class SelfTestRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_success_when_database_opens_and_manifests_are_valid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-ok-" + Guid.NewGuid().ToString("N"));
        var packsRoot = Path.Combine(tempRoot, "Packs");
        var fallbackPackDir = Path.Combine(packsRoot, "fallback");
        Directory.CreateDirectory(fallbackPackDir);

        var sourcePackDir = Path.Combine(FindRepoRoot(), "src", "Dudu.App", "Assets", "Packs", "fallback");
        File.Copy(Path.Combine(sourcePackDir, "manifest.json"), Path.Combine(fallbackPackDir, "manifest.json"));
        File.Copy(Path.Combine(sourcePackDir, "idle.png"), Path.Combine(fallbackPackDir, "idle.png"));

        try
        {
            var paths = AppPaths.ForRoot(Path.Combine(tempRoot, "Data"));

            var exitCode = await SelfTestRunner.RunAsync(paths, packsRoot, cancellationToken);

            Assert.Equal(SelfTestRunner.SuccessExitCode, exitCode);
            Assert.True(File.Exists(paths.Database));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_returns_failure_and_logs_only_step_and_exception_type_when_manifests_are_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-fail-" + Guid.NewGuid().ToString("N"));
        var missingPacksRoot = Path.Combine(tempRoot, "does-not-exist");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var paths = AppPaths.ForRoot(Path.Combine(tempRoot, "Data"));

            var exitCode = await SelfTestRunner.RunAsync(paths, missingPacksRoot, cancellationToken);

            Assert.Equal(SelfTestRunner.FailureExitCode, exitCode);

            var logPath = Path.Combine(paths.Logs, "self-test.log");
            Assert.True(File.Exists(logPath));
            var logContents = await File.ReadAllTextAsync(logPath, cancellationToken);
            Assert.Contains("manifest-validate", logContents);
            Assert.Contains("DirectoryNotFoundException", logContents);
            Assert.DoesNotContain("bundled asset packs directory is missing", logContents);
            Assert.DoesNotContain(missingPacksRoot, logContents);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_returns_failure_when_the_data_root_cannot_be_created()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempRoot, "not a directory");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);

            var exitCode = await SelfTestRunner.RunAsync(paths, Path.Combine(tempRoot, "packs"), TestContext.Current.CancellationToken);

            Assert.Equal(SelfTestRunner.FailureExitCode, exitCode);
        }
        finally
        {
            File.Delete(tempRoot);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuduDesktop.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (DuduDesktop.slnx) above {AppContext.BaseDirectory}.");
    }
}
