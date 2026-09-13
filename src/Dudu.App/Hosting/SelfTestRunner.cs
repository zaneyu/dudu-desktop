using Dudu.App.Animation;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dudu.App.Hosting;

/// <summary>
/// The <c>--self-test</c> launch mode's core logic: open the local database
/// through the exact same composition path production uses (<c>AddDuduInfrastructure</c>
/// plus <see cref="Database.InitializeAsync"/>, which runs migrations
/// idempotently), validate every shipped asset pack manifest, and resolve
/// the handful of services production resolves eagerly. No window, tray, or
/// overlay is created; no preference is written; no startup shortcut is
/// registered; the relay is never touched (no <c>RelayOptions</c> is passed
/// to <c>AddDuduInfrastructure</c>, so only <see cref="Dudu.Core.Abstractions.IPairingService"/>'s
/// offline implementation is registered). This type has no WinUI dependency
/// so it can be exercised by a unit test on a non-Windows host.
/// </summary>
public static class SelfTestRunner
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;

    private const string DatabaseOpenStep = "database-open";
    private const string ManifestValidateStep = "manifest-validate";
    private const string ServiceResolveStep = "service-resolve";

    public static async Task<int> RunAsync(
        AppPaths paths,
        string? assetPacksRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var packsRoot = assetPacksRoot
            ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Packs");

        Directory.CreateDirectory(paths.Logs);
        var logger = new SelfTestFileLogger(Path.Combine(paths.Logs, "self-test.log"));

        ServiceProvider? services = null;
        try
        {
            if (!await TryRunStepAsync(
                DatabaseOpenStep,
                logger,
                async ct =>
                {
                    services = new ServiceCollection()
                        .AddDuduInfrastructure(new DatabaseOptions(paths.Database, paths.Backups))
                        .BuildServiceProvider();
                    var database = services.GetRequiredService<Database>();
                    await database.InitializeAsync(ct);
                },
                cancellationToken))
            {
                return FailureExitCode;
            }

            if (!await TryRunStepAsync(
                ManifestValidateStep,
                logger,
                ct => ValidateManifestsAsync(packsRoot, ct),
                cancellationToken))
            {
                return FailureExitCode;
            }

            if (!await TryRunStepAsync(
                ServiceResolveStep,
                logger,
                _ =>
                {
                    ResolveCoreServices(services!);
                    return Task.CompletedTask;
                },
                cancellationToken))
            {
                return FailureExitCode;
            }

            return SuccessExitCode;
        }
        finally
        {
            if (services is not null)
            {
                await services.DisposeAsync();
            }
        }
    }

    private static async Task<bool> TryRunStepAsync(
        string step,
        ILogger logger,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PrivacySafeLog.SelfTestStepFailed(logger, step, exception.GetType().Name);
            return false;
        }
    }

    private static async Task ValidateManifestsAsync(string packsRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(packsRoot))
        {
            throw new DirectoryNotFoundException("The bundled asset packs directory is missing.");
        }

        var manifestPaths = Directory.GetDirectories(packsRoot)
            .Select(packDirectory => Path.Combine(packDirectory, "manifest.json"))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            throw new InvalidOperationException("No bundled asset pack manifests were found.");
        }

        foreach (var manifestPath in manifestPaths)
        {
            await AssetManifestLoader.LoadAsync(manifestPath, cancellationToken);
        }
    }

    private static void ResolveCoreServices(IServiceProvider services)
    {
        _ = services.GetRequiredService<IPreferencesRepository>();
        _ = services.GetRequiredService<IProfileRepository>();
        _ = services.GetRequiredService<IPairingService>();
        _ = services.GetRequiredService<IAppUnitOfWork>();
        _ = services.GetRequiredService<ICompanionFeatureTransactions>();
    }

    /// <summary>
    /// Appends privacy-safe lines (through <see cref="PrivacySafeLog"/> only) to
    /// <c>logs/self-test.log</c> under the data root. Never receives note text,
    /// keys, or exception messages — callers pass only ids, step names, and
    /// exception type names.
    /// </summary>
    private sealed class SelfTestFileLogger(string logFilePath) : ILogger
    {
        private readonly object _gate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] ({eventId.Id}) {formatter(state, null)}";
            lock (_gate)
            {
                File.AppendAllLines(logFilePath, [line]);
            }
        }
    }
}
