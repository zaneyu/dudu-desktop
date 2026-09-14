using System.Diagnostics;
using System.Globalization;
using Dudu.App.Hosting;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The stability signals task-23's eight-hour run cares about: did the process ever crash, did
/// its GDI/USER object handles or working set grow without bound, and did the database end up
/// with more than one row for the same reminder occurrence or remote note.
/// </summary>
internal readonly record struct StabilityReport(
    bool Crashed,
    double GdiHandleGrowthPercent,
    double UserHandleGrowthPercent,
    double WorkingSetGrowthMegabytesPerHour,
    int DuplicateNoteRows,
    int DuplicateReminderOccurrenceRows)
{
    public string ToJson() => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "crashed": {{(Crashed ? "true" : "false")}},
          "gdiHandleGrowthPercent": {{GdiHandleGrowthPercent}},
          "userHandleGrowthPercent": {{UserHandleGrowthPercent}},
          "workingSetGrowthMbPerHour": {{WorkingSetGrowthMegabytesPerHour}},
          "duplicateNoteRows": {{DuplicateNoteRows}},
          "duplicateReminderOccurrenceRows": {{DuplicateReminderOccurrenceRows}}
        }
        """);
}

/// <summary>
/// Reaches/exceeds thresholds per the task-23 controller ruling: a crash or any duplicate row is
/// a failure outright; handle growth and the memory-growth slope fail once they <em>exceed</em>
/// the limit (<c>&gt;</c>), matching "no growth above 5%" / "no growth slope above 1MB/hour" in
/// the brief.
/// </summary>
internal static class StabilityThresholds
{
    public const double HandleGrowthPercentLimit = 5.0d;
    public const double WorkingSetGrowthMegabytesPerHourLimit = 1.0d;

    public static IReadOnlyList<string> Evaluate(StabilityReport report)
    {
        var failures = new List<string>();
        if (report.Crashed)
        {
            failures.Add("the process exited before the run completed");
        }

        if (report.GdiHandleGrowthPercent > HandleGrowthPercentLimit)
        {
            failures.Add(
                $"GDI handle growth exceeds {HandleGrowthPercentLimit:F1}% (observed {report.GdiHandleGrowthPercent:F2}%)");
        }

        if (report.UserHandleGrowthPercent > HandleGrowthPercentLimit)
        {
            failures.Add(
                $"USER handle growth exceeds {HandleGrowthPercentLimit:F1}% (observed {report.UserHandleGrowthPercent:F2}%)");
        }

        if (report.WorkingSetGrowthMegabytesPerHour > WorkingSetGrowthMegabytesPerHourLimit)
        {
            failures.Add(
                $"working-set growth exceeds {WorkingSetGrowthMegabytesPerHourLimit:F1} MB/hour " +
                $"(observed {report.WorkingSetGrowthMegabytesPerHour:F2} MB/hour)");
        }

        if (report.DuplicateNoteRows > 0)
        {
            failures.Add($"{report.DuplicateNoteRows} duplicate remote-note row(s) found");
        }

        if (report.DuplicateReminderOccurrenceRows > 0)
        {
            failures.Add($"{report.DuplicateReminderOccurrenceRows} duplicate reminder-occurrence row(s) found");
        }

        return failures;
    }
}

/// <summary>
/// Windows-only long-run stability scenario (task 23 brief, steps 2 and 4): launches a published
/// <c>Dudu.App.exe</c>, lets it run unattended for <c>--hours</c> hours (default 8), periodically
/// samples its GDI/USER handle counts and working set, then -- after the process has exited --
/// checks its own database for duplicate reminder-occurrence or remote-note rows. Cannot run on a
/// non-Windows build host; see <c>docs/testing/windows-acceptance.md</c> for the Windows-deferred
/// eight-hour evidence this scenario is meant to produce.
/// </summary>
internal static class LongRunScenario
{
    private const int GrGdiObjects = 0;
    private const int GrUserObjects = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The long-run scenario requires Windows x64 and a published Dudu.App.exe.");
            return 3;
        }

        var executable = ReadOption(args, "--executable");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Console.Error.WriteLine("--executable <path-to-Dudu.App.exe> is required.");
            return 4;
        }

        var outputPath = ReadOption(args, "--output")
            ?? Path.Combine("artifacts", "stability", "eight-hour.json");
        var hours = ReadDoubleOption(args, "--hours") ?? 8d;
        var sampleMinutes = ReadDoubleOption(args, "--sample-minutes") ?? 5d;

        var dataRoot = Path.Combine(Path.GetTempPath(), "dudu-long-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.Environment["DUDU_DATA_ROOT"] = dataRoot;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Dudu.App.exe did not start.");

        try
        {
            var overlayHandle = await OverlayWindowLocator.WaitForOverlayWindowAsync(process.Id, TimeSpan.FromSeconds(30));
            if (overlayHandle == 0)
            {
                Console.Error.WriteLine("The pet overlay window never appeared within 30 seconds.");
                return 5;
            }

            var startTimestamp = Stopwatch.GetTimestamp();
            var totalDuration = TimeSpan.FromHours(hours);
            var sampleInterval = TimeSpan.FromMinutes(Math.Max(1d, sampleMinutes));

            process.Refresh();
            var firstGdi = OverlayWindowLocator.GetGuiResources(process.Handle, GrGdiObjects);
            var firstUser = OverlayWindowLocator.GetGuiResources(process.Handle, GrUserObjects);
            var firstWorkingSetBytes = process.WorkingSet64;
            var firstSampleTimestamp = Stopwatch.GetTimestamp();

            var lastGdi = firstGdi;
            var lastUser = firstUser;
            var lastWorkingSetBytes = firstWorkingSetBytes;
            var lastSampleTimestamp = firstSampleTimestamp;
            var crashed = false;

            while (Stopwatch.GetElapsedTime(startTimestamp) < totalDuration)
            {
                var remaining = totalDuration - Stopwatch.GetElapsedTime(startTimestamp);
                await Task.Delay(remaining < sampleInterval ? remaining : sampleInterval);

                process.Refresh();
                if (process.HasExited)
                {
                    crashed = true;
                    break;
                }

                lastGdi = OverlayWindowLocator.GetGuiResources(process.Handle, GrGdiObjects);
                lastUser = OverlayWindowLocator.GetGuiResources(process.Handle, GrUserObjects);
                lastWorkingSetBytes = process.WorkingSet64;
                lastSampleTimestamp = Stopwatch.GetTimestamp();
                Console.WriteLine(
                    $"[{DateTimeOffset.UtcNow:O}] gdi={lastGdi} user={lastUser} " +
                    $"workingSetMb={lastWorkingSetBytes / (1024.0 * 1024.0):F1}");
            }

            var elapsedHours = Stopwatch.GetElapsedTime(firstSampleTimestamp, lastSampleTimestamp).TotalHours;
            var workingSetGrowthMbPerHour = elapsedHours > 0
                ? (lastWorkingSetBytes - firstWorkingSetBytes) / (1024.0 * 1024.0) / elapsedHours
                : 0d;
            var gdiGrowthPercent = firstGdi > 0 ? (lastGdi - firstGdi) * 100.0 / firstGdi : 0d;
            var userGrowthPercent = firstUser > 0 ? (lastUser - firstUser) * 100.0 / firstUser : 0d;

            if (!crashed)
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(10_000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }

            var (duplicateNoteRows, duplicateReminderRows) = await CountDuplicateRowsAsync(dataRoot);

            var report = new StabilityReport(
                crashed,
                gdiGrowthPercent,
                userGrowthPercent,
                workingSetGrowthMbPerHour,
                duplicateNoteRows,
                duplicateReminderRows);

            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(outputPath, report.ToJson());
            Console.WriteLine($"Wrote stability report to {outputPath}");

            var failures = StabilityThresholds.Evaluate(report);
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"GATE FAILED: {failure}");
            }

            return failures.Count == 0 ? 0 : 6;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Counts rows that share a primary key after the run: <c>remote_envelopes.message_id</c> and
    /// <c>(reminder_occurrences.reminder_id, due_utc)</c> are both primary keys, so the schema
    /// already refuses true duplicates at insert time -- this check only catches a regression in
    /// that guarantee. It cannot detect a notification shown twice for the same occurrence (that
    /// would need OS notification-log auditing, out of scope here), which the Windows acceptance
    /// doc must call out as a gap rather than imply this check covers it.
    /// </summary>
    private static async Task<(int DuplicateNoteRows, int DuplicateReminderRows)> CountDuplicateRowsAsync(
        string dataRoot)
    {
        var paths = AppPaths.ForRoot(dataRoot);
        var services = new ServiceCollection()
            .AddDuduInfrastructure(new DatabaseOptions(paths.Database, paths.Backups))
            .BuildServiceProvider();
        var database = services.GetRequiredService<Database>();
        await database.InitializeAsync(CancellationToken.None);
        await using var connection = await database.CreateConnectionAsync(CancellationToken.None);

        using var noteCommand = connection.CreateCommand();
        noteCommand.CommandText =
            "SELECT (SELECT COUNT(*) FROM remote_envelopes) - (SELECT COUNT(DISTINCT message_id) FROM remote_envelopes);";
        var duplicateNoteRows = Convert.ToInt32(await noteCommand.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

        using var reminderCommand = connection.CreateCommand();
        reminderCommand.CommandText =
            "SELECT (SELECT COUNT(*) FROM reminder_occurrences) - " +
            "(SELECT COUNT(DISTINCT reminder_id || '|' || due_utc) FROM reminder_occurrences);";
        var duplicateReminderRows = Convert.ToInt32(await reminderCommand.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

        return (duplicateNoteRows, duplicateReminderRows);
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double? ReadDoubleOption(string[] args, string name) =>
        double.TryParse(ReadOption(args, name), CultureInfo.InvariantCulture, out var value) ? value : null;
}
