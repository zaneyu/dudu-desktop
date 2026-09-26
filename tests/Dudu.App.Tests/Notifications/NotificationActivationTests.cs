using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
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
            ["action"] = "reminder-snooze",
            ["reminderId"] = "reminder-2",
        });

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.ReminderSnooze, activation!.Action);
        Assert.Equal("reminder-2", activation.ReminderId);
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

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-12T10:00:00Z");

    [Fact]
    public void TryParse_resolves_an_open_reminder_body_activation()
    {
        var activation = NotificationActivation.TryParse("action=open-reminder;reminderId=reminder-3");

        Assert.NotNull(activation);
        Assert.Equal(NotificationActivationAction.OpenReminder, activation!.Action);
        Assert.Equal("reminder-3", activation.ReminderId);
        Assert.Null(NotificationActivation.TryParse("action=open-reminder&reminderId=../evil"));
    }

    [Fact]
    public async Task Note_toast_opens_the_notes_page()
    {
        var fixture = new ToastFixture();

        await fixture.HandleAsync("action=open-note&messageId=11111111-1111-4111-8111-111111111111");

        Assert.Equal(["notes"], fixture.Destinations);
    }

    [Fact]
    public async Task Reminder_toast_body_click_opens_the_reminders_page()
    {
        var fixture = new ToastFixture();

        await fixture.HandleAsync("action=open-reminder&reminderId=reminder-1");

        Assert.Equal(["reminders"], fixture.Destinations);
        Assert.Empty(fixture.Reminders.Advanced);
        Assert.Empty(fixture.Reminders.Saved);
    }

    [Fact]
    public async Task Done_button_completes_the_reminder_without_opening_a_window()
    {
        // Done/Snooze used to only navigate to the Reminders page and leave
        // the reminder exactly as it was.
        var fixture = new ToastFixture();

        await fixture.HandleAsync("action=reminder-done&reminderId=reminder-1");

        var (reminder, occurrences, next) = Assert.Single(fixture.Reminders.Advanced);
        Assert.Equal("reminder-1", reminder.Id);
        Assert.Equal(Now, Assert.Single(occurrences).DueUtc);
        Assert.Equal(Now.AddDays(1).Date, next!.Value.UtcDateTime.Date);
        Assert.Equal(["reminder-1"], fixture.DismissedToasts);
        Assert.Equal(["reminder-1"], fixture.DiscardedHeld);
        Assert.Empty(fixture.Destinations);
    }

    [Fact]
    public async Task Snooze_button_snoozes_the_reminder_for_fifteen_minutes()
    {
        var fixture = new ToastFixture();

        await fixture.HandleAsync("action=reminder-snooze&reminderId=reminder-1");

        var saved = Assert.Single(fixture.Reminders.Saved);
        Assert.Equal("reminder-1", saved.Id);
        Assert.Equal(Now + ReminderToastActions.SnoozeDuration, saved.SnoozedUntilUtc);
        Assert.Equal(fixture.Reminders.Items[0].NextDueUtc, saved.NextDueUtc);
        Assert.Equal(TimeSpan.FromMinutes(15), ReminderToastActions.SnoozeDuration);
        Assert.Equal(["reminder-1"], fixture.DismissedToasts);
        Assert.Equal(["reminder-1"], fixture.DiscardedHeld);
        Assert.Empty(fixture.Destinations);
    }

    [Theory]
    [InlineData("action=reminder-done&reminderId=missing")]
    [InlineData("action=reminder-snooze&reminderId=missing")]
    public async Task A_button_for_a_reminder_that_no_longer_exists_opens_the_reminders_page(string arguments)
    {
        var fixture = new ToastFixture();

        await fixture.HandleAsync(arguments);

        Assert.Empty(fixture.Reminders.Advanced);
        Assert.Empty(fixture.Reminders.Saved);
        Assert.Equal(["missing"], fixture.DismissedToasts);
        Assert.Equal(["reminders"], fixture.Destinations);
    }

    [Fact]
    public async Task A_failed_done_write_is_reported_and_falls_back_to_the_reminders_page()
    {
        var fixture = new ToastFixture();
        fixture.Reminders.FailAdvance = true;

        await fixture.HandleAsync("action=reminder-done&reminderId=reminder-1");

        Assert.Contains(fixture.Reporter.Operations, operation => operation == "notification-reminder-action");
        Assert.Equal(["reminders"], fixture.Destinations);
    }

    private sealed class ToastFixture
    {
        public ToastFixture()
        {
            ToastActions = new ReminderToastActions(
                new FixedClock(),
                Reminders,
                Reminders,
                (reminderId, _) =>
                {
                    DismissedToasts.Add(reminderId);
                    return Task.CompletedTask;
                },
                (reminderId, _) =>
                {
                    DiscardedHeld.Add(reminderId);
                    return Task.CompletedTask;
                },
                Reporter);
        }

        public ReminderToastActions ToastActions { get; }
        public FakeReminders Reminders { get; } = new();
        public List<string> Destinations { get; } = [];
        public List<string> DismissedToasts { get; } = [];
        public List<string> DiscardedHeld { get; } = [];
        public RecordingReporter Reporter { get; } = new();

        public Task HandleAsync(string arguments) =>
            ToastActivationRouter.HandleAsync(
                NotificationActivation.TryParse(arguments)!,
                (destination, _) =>
                {
                    Destinations.Add(destination);
                    return Task.CompletedTask;
                },
                (reminderId, _) =>
                {
                    DismissedToasts.Add(reminderId);
                    return Task.CompletedTask;
                },
                ToastActions,
                Reporter,
                TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeReminders : IReminderRepository, IReminderWriter
    {
        public List<Reminder> Items { get; } =
        [
            new Reminder(
                "reminder-1",
                "Drink water",
                null,
                true,
                new RecurrenceRule.Daily(new TimeOnly(10, 0)),
                "UTC",
                QuietHoursBehavior.DeliverImmediately,
                MissedOccurrencePolicy.LatestOnly,
                Now),
        ];

        public List<(Reminder Reminder, IReadOnlyList<ReminderOccurrence> Occurrences, DateTimeOffset? Next)> Advanced { get; } = [];
        public List<Reminder> Saved { get; } = [];
        public bool FailAdvance { get; set; }

        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(Items.ToArray());

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            if (FailAdvance) throw new InvalidOperationException("write failed");
            Advanced.Add((reminder, occurrences, nextDueUtc));
            return Task.FromResult(true);
        }

        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            Saved.Add(reminder);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReporter : IAppHostErrorReporter
    {
        public List<string> Operations { get; } = [];

        public void Report(string operation, Exception exception) => Operations.Add(operation);
    }
}
