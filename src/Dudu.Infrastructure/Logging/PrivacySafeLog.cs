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

    // Privacy: status code plus a fixed category/exception-type label only; the probe never
    // carries a token, pairing code, key, or body, so a transient outage stays distinguishable
    // from healthy-offline without leaking anything.
    [LoggerMessage(1005, LogLevel.Warning, "Sync state probe failed with {StatusCode} and category {Category}")]
    public static partial void SyncStateProbeFailed(ILogger logger, int statusCode, string category);

    // Privacy: the category is a fixed loop-state tag (e.g. needs-repair, protocol-backoff) or an
    // exception type name — never a message, token, or envelope field.
    [LoggerMessage(1006, LogLevel.Warning, "Sync loop reached terminal state {Category}")]
    public static partial void SyncLoopTerminal(ILogger logger, string category);

    // Privacy: the category is a fixed retry tag matching the reportError tag (e.g.
    // remote-sync-poll) or an exception type name — never a secret or wire field.
    [LoggerMessage(1007, LogLevel.Information, "Sync loop retrying after {Category}")]
    public static partial void SyncLoopRetry(ILogger logger, string category);

    // Privacy: status code plus an exception-type/fixed label only; the staged token value itself
    // is never passed, so a failed staging cleanup leaves a diagnostic without the credential.
    [LoggerMessage(1008, LogLevel.Warning, "Relay staging cleanup failed with {StatusCode} and category {Category}")]
    public static partial void RelayStagingCleanupFailed(ILogger logger, int statusCode, string category);

    // Privacy: status code plus a fixed promotion label only; the promoted token value itself is
    // never passed, so the 401-recovery path stays auditable without exposing the credential.
    [LoggerMessage(1009, LogLevel.Information, "Relay promoted staged token with {StatusCode} and category {Category}")]
    public static partial void RelayStagingPromoted(ILogger logger, int statusCode, string category);
}
