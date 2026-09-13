namespace Dudu.App.Notifications;

/// <summary>The action a toast activation resolved to.</summary>
public enum NotificationActivationAction
{
    OpenNote,
    ReminderDone,
    ReminderSnooze,
}

/// <summary>
/// A parsed toast activation. Executing Done/Snooze directly from the toast
/// is out of scope for this milestone; this record only carries enough to
/// navigate to the matching settings destination.
/// </summary>
public sealed record NotificationActivation(
    NotificationActivationAction Action,
    string? MessageId,
    string? ReminderId)
{
    /// <summary>
    /// Parses a raw activation-arguments string such as
    /// "action=open-note&amp;messageId=&lt;guid&gt;". Malformed or unknown
    /// input must never throw: it returns null instead so a stray or future
    /// activation shape is silently ignored rather than crashing the app.
    /// </summary>
    public static NotificationActivation? TryParse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            values[pair[..separatorIndex]] = pair[(separatorIndex + 1)..];
        }

        if (!values.TryGetValue("action", out var action))
        {
            return null;
        }

        return action switch
        {
            "open-note" when HasValue(values, "messageId", out var messageId) =>
                new NotificationActivation(NotificationActivationAction.OpenNote, messageId, null),
            "reminder-done" when HasValue(values, "reminderId", out var reminderId) =>
                new NotificationActivation(NotificationActivationAction.ReminderDone, null, reminderId),
            "reminder-snooze" when HasValue(values, "reminderId", out var reminderId) =>
                new NotificationActivation(NotificationActivationAction.ReminderSnooze, null, reminderId),
            _ => null,
        };
    }

    private static bool HasValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
