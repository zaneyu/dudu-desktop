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
        Assert.Null(activation.ReminderId);
    }

    [Fact]
    public void TryParse_resolves_a_reminder_done_activation()
    {
        var activation = NotificationActivation.TryParse("action=reminder-done&reminderId=reminder-1");

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.ReminderDone, activation!.Action);
        Assert.Equal("reminder-1", activation.ReminderId);
        Assert.Null(activation.MessageId);
    }

    [Fact]
    public void TryParse_resolves_a_reminder_snooze_activation()
    {
        var activation = NotificationActivation.TryParse("action=reminder-snooze&reminderId=reminder-2");

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.ReminderSnooze, activation!.Action);
        Assert.Equal("reminder-2", activation.ReminderId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-argument-string")]
    [InlineData("action=unknown-action&foo=bar")]
    [InlineData("action=open-note")]
    [InlineData("action=reminder-done")]
    public void TryParse_never_throws_and_returns_null_for_malformed_or_unknown_input(string? arguments)
    {
        var activation = NotificationActivation.TryParse(arguments);

        Assert.Null(activation);
    }
}
