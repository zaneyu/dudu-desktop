using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Dudu.App.Hosting;

/// <summary>
/// Counts consecutive primary runs that never reached a clean point (a stable
/// running period or a graceful shutdown). Each run increments the counter
/// before composing anything; a clean run deletes it. Once
/// <see cref="SafeModeThreshold"/> consecutive runs have failed, the next run
/// starts in safe mode. Every file operation is best-effort: the guard must
/// never itself become a startup failure.
/// </summary>
public sealed class StartupCrashGuard
{
    public const int SafeModeThreshold = 3;
    public const string FileName = "startup-crash-count";

    private readonly string? _path;
    private int _cleanMarked;

    private StartupCrashGuard(string? path, int consecutiveFailedRuns)
    {
        _path = path;
        ConsecutiveFailedRuns = consecutiveFailedRuns;
    }

    /// <summary>Failed runs recorded before this run started.</summary>
    public int ConsecutiveFailedRuns { get; }

    public bool SafeMode => ConsecutiveFailedRuns >= SafeModeThreshold;

    public static StartupCrashGuard BeginRun(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, FileName);
        var previous = 0;
        try
        {
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                previous = parsed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Dudu crash counter could not be read: {0}", exception.Message);
        }

        try
        {
            Directory.CreateDirectory(directory);
            var next = previous == int.MaxValue ? previous : previous + 1;
            File.WriteAllText(path, next.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Dudu crash counter could not be written: {0}", exception.Message);
        }

        var guard = new StartupCrashGuard(path, previous);
        ReportRunDiagnostics(directory, guard);
        return guard;
    }

    /// <summary>
    /// Correlates this run with the crash history: always records the
    /// consecutive-failed-run count seen at startup, plus an explicit entry
    /// when that count puts the run in safe mode. Writes to Trace and the
    /// <c>diagnostics.log</c> file sink only; the startup failure sink is
    /// reserved for actual failures.
    /// Best-effort: the counter file format and <see cref="MarkCleanRun"/>
    /// semantics are untouched, and this method never throws.
    /// </summary>
    private static void ReportRunDiagnostics(string directory, StartupCrashGuard guard)
    {
        try
        {
            var logsDirectory = AppPaths.ForRoot(directory).Logs;
            Trace.TraceInformation(
                "Dudu startup run began with {0} consecutive failed run(s) on record.",
                guard.ConsecutiveFailedRuns);
            FileDiagnosticLoggerProvider.AppendRedactedLine(
                logsDirectory,
                "Dudu.StartupCrashGuard",
                LogLevel.Information,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dudu startup run began with {guard.ConsecutiveFailedRuns} consecutive failed run(s) on record."));
            if (guard.SafeMode)
            {
                Trace.TraceWarning(
                    "Dudu safe mode enabled after {0} consecutive failed runs.",
                    guard.ConsecutiveFailedRuns);
                FileDiagnosticLoggerProvider.AppendRedactedLine(
                    logsDirectory,
                    "Dudu.StartupCrashGuard",
                    LogLevel.Warning,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Dudu safe mode enabled after {guard.ConsecutiveFailedRuns} consecutive failed runs (threshold {SafeModeThreshold})."));
            }
        }
        catch
        {
            // Run correlation is diagnostic only; it must never fail startup.
        }
    }

    /// <summary>Resets the counter. Idempotent.</summary>
    public void MarkCleanRun()
    {
        if (_path is null || Interlocked.Exchange(ref _cleanMarked, 1) != 0)
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Dudu crash counter could not be reset: {0}", exception.Message);
        }
    }

    /// <summary>Marks the run clean once it has stayed up for
    /// <paramref name="stablePeriod"/>. Cancellation (shutdown) simply stops
    /// the wait; the graceful-shutdown path marks clean on its own.</summary>
    public async Task MarkCleanAfterAsync(TimeSpan stablePeriod, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(stablePeriod, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        MarkCleanRun();
    }
}
