using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Notifications;

public sealed class NotificationActivationTests
{
    [Fact]
    public void TryParse_resolves_an_open_note_activation()
    {
        var activation = NotificationActivation.TryParse(
            "action=open-note&messageId=11111111-1111-4111-8111-111111111111");

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.OpenNote, activation!.Action);
        Assert.Equal("11111111-1111-4111-8111-111111111111", activation.MessageId);
        Assert.Equal("notes", activation.Destination);
    }

    [Theory]
    [InlineData("action=reminder-done&reminderId=r1")]
    [InlineData("action=reminder-snooze&reminderId=r1")]
    [InlineData("action=open-reminder&reminderId=r1")]
    [InlineData("action=reminder-done;reminderId=default-hydration")]
    [InlineData("action=reminder-snooze;reminderId=default-break")]
    [InlineData("action=open-reminder;reminderId=default-hydration")]
    public void Legacy_reminder_toast_actions_parse_to_null(string arguments)
    {
        // Reminders were removed; a reminder toast an older build left in Action Center
        // must be ignored exactly like any unknown activation, never act or open a page.
        Assert.Null(NotificationActivation.TryParse(arguments));
    }

    [Theory]
    [InlineData("reminder-done")]
    [InlineData("reminder-snooze")]
    [InlineData("open-reminder")]
    public void Legacy_reminder_toast_actions_in_the_sdk_argument_map_parse_to_null(string action)
    {
        Assert.Null(NotificationActivation.TryParse(new Dictionary<string, string>
        {
            ["action"] = action,
            ["reminderId"] = "default-hydration",
        }));
    }

    [Fact]
    public void TryParse_resolves_a_semicolon_separated_activation()
    {
        // Review I2: this is the shape the Windows App SDK actually hands back. It serializes
        // AppNotificationBuilder.AddArgument pairs semicolon-separated, so the '&'-only split
        // used to return null here and every toast click was silently ignored.
        var activation = NotificationActivation.TryParse(
            "action=open-note;messageId=11111111-1111-4111-8111-111111111111");

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.OpenNote, activation!.Action);
        Assert.Equal("11111111-1111-4111-8111-111111111111", activation.MessageId);
    }

    [Fact]
    public void TryParse_resolves_the_sdk_parsed_argument_map()
    {
        // The overload the composition actually calls, with AppNotificationActivatedEventArgs
        // .Arguments -- the SDK's own parsed map -- so no separator convention is guessed at all.
        var activation = NotificationActivation.TryParse(new Dictionary<string, string>
        {
            ["action"] = "open-note",
            ["messageId"] = "22222222-2222-4222-8222-222222222222",
        });

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.OpenNote, activation!.Action);
        Assert.Equal("22222222-2222-4222-8222-222222222222", activation.MessageId);
        Assert.Equal("notes", activation.Destination);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("11111111-1111-4111-8111-11111111111")]
    [InlineData("{11111111-1111-4111-8111-111111111111}")]
    public void TryParse_rejects_an_open_note_activation_whose_message_id_is_not_a_guid(string messageId)
    {
        // A message id is read back as a Guid downstream; anything that is not one is a
        // malformed activation, whichever overload it arrives through.
        Assert.Null(NotificationActivation.TryParse($"action=open-note;messageId={messageId}"));
        Assert.Null(NotificationActivation.TryParse(new Dictionary<string, string>
        {
            ["action"] = "open-note",
            ["messageId"] = messageId,
        }));
    }

    [Fact]
    public void TryParse_rejects_an_argument_map_with_no_message_id()
    {
        Assert.Null(NotificationActivation.TryParse(new Dictionary<string, string>
        {
            ["action"] = "open-note",
        }));
    }

    [Fact]
    public void TryParse_returns_null_for_a_null_argument_map()
    {
        Assert.Null(NotificationActivation.TryParse((IEnumerable<KeyValuePair<string, string>>?)null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-argument-string")]
    [InlineData("action=unknown-action&foo=bar")]
    [InlineData("action=open-note")]
    [InlineData("action=reminder-done")]
    [InlineData("action=open-note;messageId=")]
    public void TryParse_never_throws_and_returns_null_for_malformed_or_unknown_input(string? arguments)
    {
        var activation = NotificationActivation.TryParse(arguments);

        Assert.Null(activation);
    }
}
