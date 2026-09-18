using Dudu.App.Hosting;
using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

/// <summary>
/// Regression coverage for the last-chance reporting logic wired to the
/// process-wide handlers in <c>App.xaml.cs</c>. The <c>App</c> class itself
/// needs a WinUI dispatcher, so the headless-runnable logic lives in
/// <see cref="GlobalCrashReporting"/> and is exercised here; the wiring is
/// pinned by a source-contract test in <c>Dudu.App.Tests</c>.
/// </summary>
public sealed class GlobalCrashReportingTests
{
    [Fact]
    public void Phase_names_match_the_required_contract()
    {
        Assert.Equal("unhandled-ui", GlobalCrashReporting.PhaseUnhandledUi);
        Assert.Equal("unhandled-domain", GlobalCrashReporting.PhaseUnhandledDomain);
        Assert.Equal("unobserved-task", GlobalCrashReporting.PhaseUnobservedTask);
    }

    [Fact]
    public void Report_writes_a_redacted_phase_entry_to_startup_failure_log()
    {
        var tempRoot = NewTempRoot("dudu-crash-report-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);

            GlobalCrashReporting.Report(
                paths,
                GlobalCrashReporting.PhaseUnhandledUi,
                new InvalidOperationException(
                    "Render failed at https://private.example/overlay?token=secret-value"));

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "startup-failure.log"));
            Assert.Contains("phase=unhandled-ui", contents, StringComparison.Ordinal);
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
    public void Report_blank_phase_falls_back_to_unknown()
    {
        var tempRoot = NewTempRoot("dudu-crash-report-phase-");
        try
        {
            var paths = AppPaths.ForRoot(tempRoot);

            GlobalCrashReporting.Report(paths, "  ", new InvalidOperationException("boom"));

            var contents = File.ReadAllText(Path.Combine(paths.Logs, "startup-failure.log"));
            Assert.Contains("phase=unknown", contents, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Report_never_throws_on_null_inputs_or_unwritable_paths()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "dudu-crash-report-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");

        try
        {
            var blockedPaths = AppPaths.ForRoot(blocker);
            var exception = new InvalidOperationException("boom");

            Assert.Null(Record.Exception(() =>
                GlobalCrashReporting.Report(null, GlobalCrashReporting.PhaseUnhandledDomain, exception)));
            Assert.Null(Record.Exception(() =>
                GlobalCrashReporting.Report(blockedPaths, GlobalCrashReporting.PhaseUnhandledDomain, null)));
            Assert.Null(Record.Exception(() =>
                GlobalCrashReporting.Report(blockedPaths, GlobalCrashReporting.PhaseUnobservedTask, exception)));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ReportCurrentUser_with_no_exception_never_touches_disk()
    {
        // Null exceptions return before paths are resolved, so this exercises
        // the never-throw contract without writing to the real user profile.
        Assert.Null(Record.Exception(() =>
            GlobalCrashReporting.ReportCurrentUser(GlobalCrashReporting.PhaseUnhandledUi, null)));
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
