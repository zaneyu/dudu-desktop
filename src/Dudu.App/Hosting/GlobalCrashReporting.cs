namespace Dudu.App.Hosting;

/// <summary>
/// WinUI-free last-chance crash reporting. The <see cref="App"/> entry point
/// subscribes the process-wide handlers (WinUI render/UI thread, AppDomain,
/// unobserved tasks) and delegates here so the reporting logic itself stays
/// testable without a WinUI dispatcher. Every method is best-effort and never
/// throws: a failing crash reporter must not obscure the original failure.
/// </summary>
public static class GlobalCrashReporting
{
    public const string PhaseUnhandledUi = "unhandled-ui";
    public const string PhaseUnhandledDomain = "unhandled-domain";
    public const string PhaseUnobservedTask = "unobserved-task";

    /// <summary>
    /// Records <paramref name="exception"/> under <paramref name="phase"/> in
    /// the current user's log directory. Resolves paths defensively so a
    /// broken environment cannot throw out of a global exception handler.
    /// </summary>
    public static void ReportCurrentUser(string? phase, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        AppPaths? paths = null;
        try
        {
            paths = AppPaths.ForCurrentUser();
        }
        catch
        {
            // Without resolvable paths there is nowhere to persist the crash.
        }

        Report(paths, phase, exception);
    }

    /// <summary>
    /// Records <paramref name="exception"/> under <paramref name="phase"/>
    /// via <see cref="StartupFailureLogger"/>, which redacts URLs and
    /// secret-shaped values before persisting. Never throws.
    /// </summary>
    public static void Report(AppPaths? paths, string? phase, Exception? exception)
    {
        if (paths is null || exception is null)
        {
            return;
        }

        try
        {
            StartupFailureLogger.Record(
                paths,
                string.IsNullOrWhiteSpace(phase) ? "unknown" : phase,
                exception);
        }
        catch
        {
            // Crash reporting must never change the app's existing failure
            // behavior or obscure the original exception.
        }
    }
}
