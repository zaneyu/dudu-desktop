using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// Pins the field-diagnostics wiring that cannot run headless (it needs a
/// WinUI dispatcher or the production composition): the last-chance handlers
/// in <c>App.xaml.cs</c>, the file-sink registration, and the secondary-exit
/// log line. Behavioral coverage for the WinUI-free helpers lives in
/// <c>Dudu.Infrastructure.Tests</c>, which links the same sources.
/// </summary>
public sealed class FieldDiagnosticsContractTests
{
    [Fact]
    public void App_subscribes_all_three_last_chance_handlers()
    {
        var app = ReadSource("App.xaml.cs");

        Assert.Contains("UnhandledException += OnUnhandledException", app, StringComparison.Ordinal);
        Assert.Contains(
            "AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskScheduler.UnobservedTaskException += OnUnobservedTaskException",
            app,
            StringComparison.Ordinal);

        Assert.Contains(
            "GlobalCrashReporting.ReportCurrentUser",
            app,
            StringComparison.Ordinal);
        Assert.Contains("GlobalCrashReporting.PhaseUnhandledUi", app, StringComparison.Ordinal);
        Assert.Contains("GlobalCrashReporting.PhaseUnhandledDomain", app, StringComparison.Ordinal);
        Assert.Contains("GlobalCrashReporting.PhaseUnobservedTask", app, StringComparison.Ordinal);
        Assert.Contains("e.SetObserved()", app, StringComparison.Ordinal);

        // Existing behavior stays untouched.
        Assert.Contains("ReportStartupFailure", app, StringComparison.Ordinal);
        Assert.Contains("ObserveStartupAsync", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_composition_registers_the_file_sink_before_building_services()
    {
        var composition = ReadSource(Path.Combine("Hosting", "WindowsCompanionProductionComposition.cs"));

        var registration = composition.IndexOf(
            ".AddFileDiagnosticLogging(paths)",
            StringComparison.Ordinal);
        var build = composition.IndexOf("BuildServiceProvider()", StringComparison.Ordinal);

        Assert.True(registration >= 0);
        Assert.True(build > registration);
    }

    [Fact]
    public void Secondary_exit_is_logged_to_the_file_sink()
    {
        var bootstrap = ReadSource(Path.Combine("Hosting", "WindowsCompanionBootstrap.cs"));

        Assert.Contains("ReportSecondaryExit()", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Dudu.SingleInstance", bootstrap, StringComparison.Ordinal);
        Assert.Contains("AppendRedactedLine", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashGuard_correlation_uses_the_file_sink_not_the_failure_log()
    {
        var guard = ReadSource(Path.Combine("Hosting", "StartupCrashGuard.cs"));

        Assert.Contains("AppendRedactedLine", guard, StringComparison.Ordinal);
        Assert.Contains("safe mode enabled", guard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consecutive failed run", guard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startup-failure", guard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MarkCleanRun", guard, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "src", "Dudu.App", relativePath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output path.");
    }
}
