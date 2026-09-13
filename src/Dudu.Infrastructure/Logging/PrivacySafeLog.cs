using Microsoft.Extensions.Logging;

namespace Dudu.Infrastructure.Logging;

/// <summary>
/// The single logging surface for the relay/remote-sync path. Every method here accepts only
/// ids, counts, status codes, and exception/rejection categories — never a note's plaintext, a
/// token, a pairing code, a public/private key, a ciphertext, or a raw HTTP header/body. Callers
/// must not call <c>ILogger</c> directly for anything that touches the relay or an envelope;
/// route it through one of these methods instead so this file stays the one place privacy
/// leakage through logs can be reviewed.
/// </summary>
public static partial class PrivacySafeLog
{
    [LoggerMessage(1001, LogLevel.Information, "Relay poll returned {MessageCount} envelopes")]
    public static partial void PollCompleted(ILogger logger, int messageCount);

    [LoggerMessage(1002, LogLevel.Warning, "Relay request failed with {StatusCode} and category {Category}")]
    public static partial void RelayFailed(ILogger logger, int statusCode, string category);

    [LoggerMessage(1003, LogLevel.Warning, "Envelope {MessageId} was rejected as {Reason}")]
    public static partial void EnvelopeRejected(ILogger logger, Guid messageId, string reason);

    /// <summary>
    /// Logged by <c>--self-test</c> when a step fails. Carries only the step name
    /// and the failing exception's type name — never the exception's message,
    /// which could contain a user-specific file path or other local detail.
    /// </summary>
    [LoggerMessage(1004, LogLevel.Error, "Self-test step {Step} failed with exception type {ExceptionType}")]
    public static partial void SelfTestStepFailed(ILogger logger, string step, string exceptionType);
}
