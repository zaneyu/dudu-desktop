using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dudu.App.Hosting;

/// <summary>
/// File-backed diagnostics sink for the <c>Logs</c> directory. The previous
/// <c>DiagnosticAppHostErrorReporter</c> fallback (Trace + Console) evaporated
/// without a debugger attached, so field failures were lost; loggers created
/// through this provider append redacted lines to <c>diagnostics.log</c>
/// instead. Privacy properties match <see cref="StartupFailureLogger"/>: URLs
/// and secret-shaped values are redacted before anything is persisted, the
/// file is capped and trimmed to the most recent entries, and every I/O
/// failure is swallowed so diagnostics can never crash the app.
/// </summary>
public sealed class FileDiagnosticLoggerProvider : ILoggerProvider
{
    public const string FileName = "diagnostics.log";

    internal const int MaxLogBytes = 128 * 1024;

    private static readonly object Gate = new();
    private readonly string _logsDirectory;

    public FileDiagnosticLoggerProvider(AppPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).Logs)
    {
    }

    public FileDiagnosticLoggerProvider(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = logsDirectory;
    }

    public string LogsDirectory => _logsDirectory;

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new FileDiagnosticLogger(_logsDirectory, categoryName);
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Appends one redacted line to <c>diagnostics.log</c> under
    /// <paramref name="logsDirectory"/>, trimming the file to the most recent
    /// entries. Best-effort: never throws. Callers must pass only fixed,
    /// code-controlled strings plus counts/statuses — never note plaintext,
    /// tokens, pairing codes, keys, ciphertext, or request/response bodies.
    /// </summary>
    internal static void AppendRedactedLine(
        string? logsDirectory,
        string category,
        LogLevel level,
        string message,
        Exception? exception = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logsDirectory))
            {
                return;
            }

            var safeCategory = string.IsNullOrWhiteSpace(category) ? "Dudu" : category;
            var entry = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} {level} {safeCategory}: {message ?? string.Empty}");
            if (exception is not null)
            {
                entry += Environment.NewLine + exception.ToString();
            }

            var redacted = StartupFailureLogger.Redact(entry);
            lock (Gate)
            {
                Directory.CreateDirectory(logsDirectory);
                var logPath = Path.Combine(logsDirectory, FileName);
                File.AppendAllText(logPath, redacted + Environment.NewLine);
                TrimToRecentBytes(logPath);
            }
        }
        catch
        {
            // Diagnostics must never change the app's behavior or obscure the
            // original failure.
        }
    }

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

/// <summary>
/// An <see cref="ILogger"/> that persists through
/// <see cref="FileDiagnosticLoggerProvider"/>. Scopes are not supported (the
/// sink records flat, self-contained lines); <see cref="ILogger.IsEnabled"/>
/// accepts every level except <see cref="LogLevel.None"/>.
/// </summary>
public sealed class FileDiagnosticLogger : ILogger
{
    private readonly string _logsDirectory;
    private readonly string _category;

    internal FileDiagnosticLogger(string logsDirectory, string category)
    {
        _logsDirectory = logsDirectory;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
        {
            return;
        }

        string message;
        try
        {
            message = formatter(state, exception) ?? string.Empty;
        }
        catch
        {
            message = string.Empty;
        }

        FileDiagnosticLoggerProvider.AppendRedactedLine(
            _logsDirectory,
            _category,
            logLevel,
            message,
            exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Minimal single-sink <see cref="ILoggerFactory"/> over
/// <see cref="FileDiagnosticLoggerProvider"/>. Only
/// <c>Microsoft.Extensions.Logging.Abstractions</c> is referenced, so the
/// full logging implementation is intentionally not available; this factory
/// exists so <c>AppHost.ResolveErrorReporter</c> finds a factory whose
/// loggers persist to <c>diagnostics.log</c> instead of falling back to
/// Trace + Console.
/// </summary>
public sealed class FileDiagnosticLoggerFactory : ILoggerFactory
{
    private readonly FileDiagnosticLoggerProvider _provider;

    public FileDiagnosticLoggerFactory(FileDiagnosticLoggerProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Registers the file diagnostics sink so <c>AppHost.ResolveErrorReporter</c>
/// resolves an <see cref="ILoggerFactory"/> backed by
/// <see cref="FileDiagnosticLoggerProvider"/>.
/// </summary>
public static class FileDiagnosticLoggingServiceExtensions
{
    public static IServiceCollection AddFileDiagnosticLogging(
        this IServiceCollection services,
        AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        var provider = new FileDiagnosticLoggerProvider(paths);
        services.AddSingleton(provider);
        services.AddSingleton<ILoggerFactory>(new FileDiagnosticLoggerFactory(provider));
        return services;
    }
}
