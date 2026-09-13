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
}
