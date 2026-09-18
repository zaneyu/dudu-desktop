using Dudu.App.Hosting;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Regression coverage for the <see cref="StartupCrashGuard"/> run-correlation
/// diagnostics: the consecutive-failed-run count logged at every
/// <c>BeginRun</c> and the explicit entry when safe mode engages. Counter file
/// format and clean-run semantics are pinned as unchanged.
/// </summary>
public sealed class StartupCrashGuardDiagnosticsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dudu-crash-guard-diag-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BeginRun_records_the_consecutive_failed_run_count_in_diagnostics_log()
    {
        var first = StartupCrashGuard.BeginRun(_directory);
        Assert.Equal(0, first.ConsecutiveFailedRuns);
        Assert.Contains(
            "0 consecutive failed run",
            ReadDiagnosticsLog(),
            StringComparison.Ordinal);

        var second = StartupCrashGuard.BeginRun(_directory);
        Assert.Equal(1, second.ConsecutiveFailedRuns);
        Assert.Contains(
            "1 consecutive failed run",
            ReadDiagnosticsLog(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SafeMode_transition_writes_an_explicit_entry()
    {
        for (var run = 0; run < StartupCrashGuard.SafeModeThreshold; run++)
        {
            Assert.False(StartupCrashGuard.BeginRun(_directory).SafeMode);
        }

        var guard = StartupCrashGuard.BeginRun(_directory);

        Assert.True(guard.SafeMode);
        Assert.Contains(
            "safe mode enabled",
            ReadDiagnosticsLog(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Counter_file_format_is_still_a_plain_integer()
    {
        StartupCrashGuard.BeginRun(_directory);

        Assert.Equal(
            "1",
            File.ReadAllText(Path.Combine(_directory, StartupCrashGuard.FileName)).Trim());
    }

    [Fact]
    public void Run_diagnostics_never_fail_the_run()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "dudu-crash-guard-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");

        try
        {
            StartupCrashGuard? guard = null;
            var exception = Record.Exception(() => guard = StartupCrashGuard.BeginRun(blocker));

            Assert.Null(exception);
            Assert.NotNull(guard);
            Assert.Equal(0, guard!.ConsecutiveFailedRuns);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string ReadDiagnosticsLog() =>
        File.ReadAllText(Path.Combine(_directory, "logs", "diagnostics.log"));
}
