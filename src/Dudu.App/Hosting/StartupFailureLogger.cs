using System.Text.RegularExpressions;

namespace Dudu.App.Hosting;

/// <summary>
/// Best-effort diagnostics for failures that prevent the desktop companion from
/// staying open. The log is deliberately local and redacts URLs and
/// token-shaped values before anything is persisted.
/// </summary>
public static class StartupFailureLogger
{
    public const string FileName = "startup-failure.log";

    private const int MaxLogBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SecretPattern = new(
        @"\b(?<name>token|access_token|refresh_token|pairing_code|password|secret|private_key)\s*[:=]\s*[^,\s;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static void Record(AppPaths paths, string phase, Exception exception)
    {
        if (paths is null || exception is null)
        {
            return;
        }

        try
        {
            var safePhase = SanitizeField(phase);
            var safeException = Redact(exception.ToString());
            var entry = string.Create(
                global::System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} phase={safePhase} hresult=0x{exception.HResult:X8}{Environment.NewLine}{safeException}{Environment.NewLine}");

            lock (Gate)
            {
                Directory.CreateDirectory(paths.Logs);
                var logPath = Path.Combine(paths.Logs, FileName);
                File.AppendAllText(logPath, entry);
                TrimToRecentBytes(logPath);
            }
        }
        catch
        {
            // Startup diagnostics must never change the app's existing failure
            // behavior or obscure the original exception.
        }
    }

    private static string Redact(string value)
    {
        var redacted = UrlPattern.Replace(value, "[redacted-url]");
        return SecretPattern.Replace(
            redacted,
            match => $"{match.Groups["name"].Value}=[redacted]");
    }

    private static string SanitizeField(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void TrimToRecentBytes(string logPath)
    {
        var bytes = File.ReadAllBytes(logPath);
        if (bytes.Length <= MaxLogBytes)
        {
            return;
        }

        var recent = bytes[^MaxLogBytes..];
        File.WriteAllBytes(logPath, recent);
    }
}
