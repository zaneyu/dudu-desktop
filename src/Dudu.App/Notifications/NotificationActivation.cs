namespace Dudu.App.Notifications;

/// <summary>The action a toast activation resolved to.</summary>
public enum NotificationActivationAction
{
    OpenNote,
    ReminderDone,
    ReminderSnooze,
    /// <summary>A click on a reminder toast's body (not one of its buttons).</summary>
    OpenReminder,
}

/// <summary>
/// A parsed toast activation: which toast (or toast button) was clicked and
/// the id it carries. NotificationInvocationRouter acts on it.
/// </summary>
public sealed record NotificationActivation(
    NotificationActivationAction Action,
    string? MessageId,
    string? ReminderId)
{
    /// <summary>
    /// Parses a raw activation-arguments string such as
    /// "action=open-note&amp;messageId=&lt;guid&gt;" or the semicolon-separated
    /// form the Windows App SDK produces from
    /// <c>AppNotificationBuilder.AddArgument</c>. Prefer the
    /// <see cref="TryParse(IEnumerable{KeyValuePair{string, string}}?)"/>
    /// overload when the SDK already handed back a parsed map. Malformed or
    /// unknown input must never throw: it returns null instead so a stray or
    /// future activation shape is silently ignored rather than crashing the app.
    /// </summary>
    public static NotificationActivation? TryParse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in NotificationArguments.Parse(arguments))
        {
            values[key] = value;
        }

        return Resolve(values);
    }

    /// <summary>
    /// Parses the argument map the Windows App SDK itself produces
    /// (<c>AppNotificationActivatedEventArgs.Arguments</c>), so no separator
    /// convention has to be guessed at all. Returns null for a null map or an
    /// activation this build does not understand.
    /// </summary>
    public static NotificationActivation? TryParse(IEnumerable<KeyValuePair<string, string>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            if (pair.Key is not null && pair.Value is not null)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return Resolve(values);
    }

    private static NotificationActivation? Resolve(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("action", out var action))
        {
            return null;
        }

        return action switch
        {
            "open-note" when HasMessageId(values, out var messageId) =>
                new NotificationActivation(NotificationActivationAction.OpenNote, messageId, null),
            "reminder-done" when HasValue(values, "reminderId", out var reminderId) =>
                new NotificationActivation(NotificationActivationAction.ReminderDone, null, reminderId),
            "reminder-snooze" when HasValue(values, "reminderId", out var reminderId) =>
                new NotificationActivation(NotificationActivationAction.ReminderSnooze, null, reminderId),
            "open-reminder" when HasValue(values, "reminderId", out var reminderId) =>
                new NotificationActivation(NotificationActivationAction.OpenReminder, null, reminderId),
            _ => null,
        };
    }

    /// <summary>
    /// The settings page this activation belongs to: Love Notes for a note
    /// toast, Reminders for anything on a reminder toast. Used to open the
    /// page for a body click, and as the fallback when a Done/Snooze button
    /// could not be carried out in the background.
    /// </summary>
    public string Destination => Action switch
    {
        NotificationActivationAction.OpenNote => "notes",
        _ => "reminders",
    };

    /// <summary>
    /// A message id is looked up as a <see cref="Guid"/> downstream, so an
    /// activation carrying anything else is malformed and must be dropped here
    /// rather than turned into a lookup that can only miss. "D" format only:
    /// the app always writes the plain hyphenated form.
    /// </summary>
    private static bool HasMessageId(IReadOnlyDictionary<string, string> values, out string messageId)
    {
        if (HasValue(values, "messageId", out var candidate) &&
            Guid.TryParseExact(candidate, "D", out _))
        {
            messageId = candidate;
            return true;
        }

        messageId = string.Empty;
        return false;
    }

    private static bool HasValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var found) && IsConstrainedId(found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Constrains toast argument ids to a safe alphabet so a crafted
    /// activation cannot inject path/query content downstream. Non-empty,
    /// max 128 chars, ASCII letters/digits plus '-' and '_' only.</summary>
    private static bool IsConstrainedId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128)
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if ((ch >= 'A' && ch <= 'Z')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch == '-' || ch == '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
