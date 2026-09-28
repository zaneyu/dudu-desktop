using Dudu.App.Hosting;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Remote;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// Exercises the <c>--self-test</c> code path (<see cref="SelfTestRunner.RunAsync"/>,
/// reached from App.xaml.cs's <c>OnLaunched</c> via <see cref="AppPaths.ForCurrentUser"/>)
/// against a temporary <c>DUDU_DATA_ROOT</c>, without any WinUI window, tray,
/// overlay, preference write, startup registration, or relay connection.
///
/// This project's tests compile on macOS but require Windows to execute (see
/// the host boundary section of task-22-context.md); the equivalent
/// runnable coverage lives in
/// Dudu.Infrastructure.Tests.Hosting.SelfTestRunnerTests, which links the
/// same WinUI-free source files and actually runs on this host.
/// </summary>
[Collection("DUDU_DATA_ROOT environment variable")]
public sealed class SelfTestRunnerTests
{
    [Fact]
    public async Task RunAsync_via_AppPaths_ForCurrentUser_returns_success_when_database_opens_and_manifests_are_valid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-apptests-ok-" + Guid.NewGuid().ToString("N"));
        var packsRoot = Path.Combine(tempRoot, "Packs");
        var fallbackPackDir = Path.Combine(packsRoot, "fallback");
        Directory.CreateDirectory(fallbackPackDir);

        var sourcePackDir = Path.Combine(FindRepoRoot(), "src", "Dudu.App", "Assets", "Packs", "fallback");
        File.Copy(Path.Combine(sourcePackDir, "manifest.json"), Path.Combine(fallbackPackDir, "manifest.json"));
        File.Copy(Path.Combine(sourcePackDir, "idle.png"), Path.Combine(fallbackPackDir, "idle.png"));

        var previousDataRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", Path.Combine(tempRoot, "Data"));
            var paths = AppPaths.ForCurrentUser();

            var exitCode = await SelfTestRunner.RunAsync(paths, packsRoot, cancellationToken);

            Assert.Equal(SelfTestRunner.SuccessExitCode, exitCode);
            Assert.True(File.Exists(paths.Database));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previousDataRoot);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_via_AppPaths_ForCurrentUser_returns_failure_when_manifests_are_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-apptests-fail-" + Guid.NewGuid().ToString("N"));
        var missingPacksRoot = Path.Combine(tempRoot, "does-not-exist");
        Directory.CreateDirectory(tempRoot);

        var previousDataRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", Path.Combine(tempRoot, "Data"));
            var paths = AppPaths.ForCurrentUser();

            var exitCode = await SelfTestRunner.RunAsync(paths, missingPacksRoot, cancellationToken);

            Assert.Equal(SelfTestRunner.FailureExitCode, exitCode);

            var logPath = Path.Combine(paths.Logs, "self-test.log");
            Assert.True(File.Exists(logPath));
            var logContents = await File.ReadAllTextAsync(logPath, cancellationToken);
            Assert.Contains("manifest-validate", logContents);
            Assert.Contains("DirectoryNotFoundException", logContents);
            Assert.DoesNotContain(missingPacksRoot, logContents);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previousDataRoot);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core_service_check_passes_on_the_production_shaped_container_and_writes_nothing(bool relayConfigured)
    {
        // The reduced cute-companion service graph, built exactly as production
        // builds it (AddDuduInfrastructure, offline or with a relay configured),
        // on a database that already has a preferences row like an existing
        // install. The check must resolve every core service and only read.
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "dudu-self-test-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var services = new ServiceCollection()
            .AddDuduInfrastructure(
                new DatabaseOptions(Path.Combine(tempRoot, "dudu.db"), Path.Combine(tempRoot, "backups")),
                relayConfigured ? new RelayOptions(new Uri("https://relay.invalid/")) : null)
            .BuildServiceProvider();
        try
        {
            await services.GetRequiredService<Database>().InitializeAsync(cancellationToken);
            var saved = Preferences.Default with { ReducedMotion = true, SoundVolume = 0.5 };
            var preferences = services.GetRequiredService<IPreferencesRepository>();
            await preferences.SaveAsync(saved, cancellationToken);

            await SelfTestRunner.CheckCoreServicesAsync(services, cancellationToken);

            Assert.NotEmpty(SelfTestRunner.CoreServiceTypes);
            Assert.All(SelfTestRunner.CoreServiceTypes, type => Assert.NotNull(services.GetService(type)));
            Assert.Contains(typeof(IAppUnitOfWork), SelfTestRunner.CoreServiceTypes);
            Assert.Contains(typeof(ICompanionFeatureTransactions), SelfTestRunner.CoreServiceTypes);
            Assert.Equal(saved, await preferences.GetAsync(cancellationToken));
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(tempRoot, recursive: true);
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
