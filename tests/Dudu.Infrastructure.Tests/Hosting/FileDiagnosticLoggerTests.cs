using Dudu.App.Hosting;
using Dudu.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Regression coverage for the persisted <c>diagnostics.log</c> sink. These
/// tests link <c>src/Dudu.App/Hosting/FileDiagnosticLogger.cs</c> directly so
/// they run headless (the same pattern as <c>StartupFailureLoggerTests</c>).
/// </summary>
public sealed class FileDiagnosticLoggerTests
{
    [Fact]
    public void Log_persists_a_redacted_entry_to_diagnostics_log()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);
            var provider = new FileDiagnosticLoggerProvider(paths);
            ILogger logger = provider.CreateLogger("Dudu.AppHost");

            logger.LogError(
                new InvalidOperationException(
                    "Sync failed at https://private.example/relay with token=secret-value"),
                "Dudu AppHost operation {Operation} failed.",
                "remote-sync-start");

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "diagnostics.log"));
            Assert.Contains("Error", contents, StringComparison.Ordinal);
            Assert.Contains("Dudu.AppHost", contents, StringComparison.Ordinal);
            Assert.Contains("remote-sync-start", contents, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), contents, StringComparison.Ordinal);
            Assert.Contains("[redacted-url]", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("private.example", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", contents, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Log_keeps_the_file_bounded_and_trims_to_recent_entries()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-size-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);
            var provider = new FileDiagnosticLoggerProvider(paths);
            ILogger logger = provider.CreateLogger("Dudu.AppHost");

            logger.LogError(new InvalidOperationException(new string('b', 200_000)), "First flood.");
            logger.LogError(new InvalidOperationException("last failure"), "Last entry.");

            var logPath = Path.Combine(paths.Logs, "diagnostics.log");
            Assert.True(
                new FileInfo(logPath).Length <= FileDiagnosticLoggerProvider.MaxLogBytes);
            Assert.Contains("Last entry.", File.ReadAllText(logPath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Log_swallows_write_failures()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "dudu-diag-log-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");

        try
        {
            var provider = new FileDiagnosticLoggerProvider(blocker);
            ILogger logger = provider.CreateLogger("Dudu.AppHost");

            var exception = Record.Exception(() =>
                logger.LogError(
                    new InvalidOperationException("diagnostics failed"),
                    "Blocked write."));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Composition_registers_a_factory_backed_by_the_file_provider()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-composition-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);
            using var services = new ServiceCollection()
                .AddFileDiagnosticLogging(paths)
                .BuildServiceProvider();

            var factory = services.GetService<ILoggerFactory>();
            Assert.NotNull(factory);
            Assert.IsType<FileDiagnosticLoggerFactory>(factory);
            Assert.NotNull(services.GetService<FileDiagnosticLoggerProvider>());

            // This is the exact call AppHost.ResolveErrorReporter makes: a
            // logger for "Dudu.AppHost" must persist instead of evaporating.
            factory.CreateLogger("Dudu.AppHost").LogError(
                new InvalidOperationException("composite check failed"),
                "Dudu AppHost operation {Operation} failed.",
                "presentation-scheduler");

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "diagnostics.log"));
            Assert.Contains("presentation-scheduler", contents, StringComparison.Ordinal);
            Assert.Contains("composite check failed", contents, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Composition_gives_optional_typed_loggers_a_file_logger_not_null()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-typed-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);
            using var services = new ServiceCollection()
                .AddFileDiagnosticLogging(paths)
                .AddSingleton<OptionalLoggerConsumer>()
                .BuildServiceProvider();

            // Same constructor shape as RelayClient/RemoteSyncService: an optional
            // ILogger<T> the container used to fill with its default (null).
            var consumer = services.GetRequiredService<OptionalLoggerConsumer>();
            Assert.NotNull(consumer.Logger);

            PrivacySafeLog.RelayFailed(consumer.Logger!, 404, "not-found");
            PrivacySafeLog.SyncStateProbeFailed(consumer.Logger!, 0, "RelayNotFoundException");

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "diagnostics.log"));
            Assert.Contains("Relay request failed with 404 and category not-found", contents, StringComparison.Ordinal);
            Assert.Contains("Sync state probe failed", contents, StringComparison.Ordinal);
            Assert.Contains(nameof(OptionalLoggerConsumer) + ":", contents, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Routine_poll_heartbeats_stay_out_of_the_file_but_failures_do_not()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-heartbeat-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);
            ILogger logger = new FileDiagnosticLoggerProvider(paths).CreateLogger("Dudu.Infrastructure.Remote.RelayClient");

            for (var poll = 0; poll < 50; poll++)
            {
                PrivacySafeLog.PollCompleted(logger, 0);
            }

            PrivacySafeLog.RelayFailed(logger, 0, "timeout");

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "diagnostics.log"));
            Assert.DoesNotContain("Relay poll returned", contents, StringComparison.Ordinal);
            Assert.Contains("Relay request failed with 0 and category timeout", contents, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void CreateLogger_rejects_a_blank_category()
    {
        var tempRoot = NewTempRoot("dudu-diag-log-category-");
        try
        {
            var provider = new FileDiagnosticLoggerProvider(AppPaths.ForRoot(tempRoot).Logs);

            Assert.Throws<ArgumentException>(() => provider.CreateLogger("  "));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private sealed class OptionalLoggerConsumer(ILogger<OptionalLoggerConsumer>? logger = null)
    {
        public ILogger<OptionalLoggerConsumer>? Logger { get; } = logger;
    }

    private static string NewTempRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
