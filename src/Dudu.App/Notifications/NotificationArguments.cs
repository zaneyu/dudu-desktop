namespace Dudu.App.Notifications;

/// <summary>
/// The single key=value parser for toast activation arguments, shared by the
/// writer (<see cref="WindowsAppNotificationSink"/>, which splits a request's
/// argument string into AddArgument pairs) and the reader
/// (<see cref="NotificationActivation"/>, which parses an activation back).
/// Review I2 / parked T15: these two had separate copies that split on '&'
/// only, while the Windows App SDK serializes AddArgument pairs
/// semicolon-separated -- so every toast click parsed to nothing. Accepting
/// both separators in one place is what keeps the round trip honest.
/// </summary>
internal static class NotificationArguments
{
    private static readonly char[] PairSeparators = ['&', ';'];

    /// <summary>
    /// Splits "a=1&amp;b=2" or "a=1;b=2" into its pairs. Never throws: a
    /// fragment without a '=' , or with an empty key, is skipped so a stray or
    /// future activation shape is ignored rather than crashing the app.
    /// </summary>
    public static IEnumerable<(string Key, string Value)> Parse(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            yield break;
        }

        foreach (var pair in arguments.Split(PairSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            yield return (
                Uri.UnescapeDataString(pair[..separatorIndex]),
                Uri.UnescapeDataString(pair[(separatorIndex + 1)..]));
        }
    }

    /// <summary>Builds one "key=value" fragment with both halves
    /// percent-encoded, so a value containing '&amp;', ';', '=' or '%' can
    /// never split into, or forge, another argument.</summary>
    public static string Pair(string key, string value) =>
        Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
}
