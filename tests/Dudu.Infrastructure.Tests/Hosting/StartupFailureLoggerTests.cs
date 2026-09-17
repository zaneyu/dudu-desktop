using Dudu.App.Hosting;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

public sealed class StartupFailureLoggerTests
{
    [Fact]
    public void Record_writes_startup_details_to_the_data_root_and_redacts_sensitive_values()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "dudu-startup-log-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForRoot(tempRoot);

        try
        {
            var exception = new InvalidOperationException(
                "Overlay failed at https://private.example/relay?token=secret-value");

            StartupFailureLogger.Record(paths, "overlay-create", exception);

            var logPath = Path.Combine(paths.Logs, "startup-failure.log");
            Assert.True(File.Exists(logPath));
            var contents = File.ReadAllText(logPath);
            Assert.Contains("phase=overlay-create", contents);
            Assert.Contains(nameof(InvalidOperationException), contents);
            Assert.Contains("Overlay failed", contents);
            Assert.Contains("[redacted-url]", contents);
            Assert.DoesNotContain("private.example", contents);
            Assert.DoesNotContain("secret-value", contents);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_keeps_the_log_bounded_to_the_recent_entries()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "dudu-startup-log-size-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.ForRoot(tempRoot);

        try
        {
            StartupFailureLogger.Record(
                paths,
                "first",
                new InvalidOperationException(new string('a', 80_000)));
            StartupFailureLogger.Record(
                paths,
                "last",
                new InvalidOperationException("last failure"));

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "startup-failure.log"));
            Assert.True(new FileInfo(Path.Combine(paths.Logs, "startup-failure.log")).Length <= 64 * 1024);
            Assert.Contains("phase=last", contents);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_swallows_log_write_failures()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "dudu-startup-log-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempRoot, "not a directory");

        try
        {
            var paths = AppPaths.ForRoot(tempRoot);

            var exception = Record.Exception(() =>
                StartupFailureLogger.Record(
                    paths,
                    "blocked",
                    new InvalidOperationException("startup failed")));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(tempRoot);
        }
    }
}
