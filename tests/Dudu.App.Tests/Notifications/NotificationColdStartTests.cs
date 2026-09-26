using Dudu.App.Notifications;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Notifications;

/// <summary>
/// A toast click that launches Dudu (cold start) is handled by App once the
/// runtime is composed. A reminder body click (open-reminder) used to map to
/// no page there, and Done/Snooze only opened Reminders instead of being
/// carried out. App now hands the parsed activation to the same
/// <see cref="NotificationInvocationRouter"/> used while running.
/// </summary>
public sealed class NotificationColdStartTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("action=reminder-done&reminderId=r-1", true)]
    [InlineData("action=reminder-snooze&reminderId=r-1", true)]
    [InlineData("action=open-reminder&reminderId=r-1", false)]
    [InlineData("action=open-note&messageId=11111111-1111-4111-8111-111111111111", false)]
    public void Only_done_and_snooze_are_answered_in_the_background(string arguments, bool background)
    {
        Assert.Equal(background, NotificationInvocationRouter.ActsInBackground(NotificationActivation.TryParse(arguments)));
    }

    [Fact]
    public void Nothing_is_answered_in_the_background_without_an_activation() =>
        Assert.False(NotificationInvocationRouter.ActsInBackground(null));

    [Fact]
    public async Task A_cold_start_body_click_opens_the_reminders_page()
    {
        var navigated = new List<string>();
        var router = new NotificationInvocationRouter(
            (destination, _) => { navigated.Add(destination); return Task.CompletedTask; },
            () => null,
            (_, _) => Task.CompletedTask);

        await router.HandleActivationAsync(
            NotificationActivation.TryParse("action=open-reminder&reminderId=r-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["reminders"], navigated);
    }

    [Fact]
    public async Task A_cold_start_snooze_is_carried_out_without_opening_settings()
    {
        var reminder = new Reminder(
            "r-cold",
            "Stretch",
            null,
            true,
            new RecurrenceRule.Once(),
            "UTC",
            QuietHoursBehavior.DeliverImmediately,
            MissedOccurrencePolicy.LatestOnly,
            null);
        var repository = new SingleRowRepository(reminder);
        var dismissed = new List<string>();
        var actions = new ReminderToastActions(
            new FixedClock(NineAm),
            repository,
            (id, _) => { dismissed.Add(id); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);
        var navigated = new List<string>();
        var router = new NotificationInvocationRouter(
            (destination, _) => { navigated.Add(destination); return Task.CompletedTask; },
            () => actions,
            (_, _) => Task.CompletedTask);

        // The one-time reminder fired in the previous run (NextDueUtc null).
        await router.HandleActivationAsync(
            NotificationActivation.TryParse("action=reminder-snooze&reminderId=r-cold"),
            TestContext.Current.CancellationToken);

        Assert.Empty(navigated);
        Assert.Equal(["r-cold"], dismissed);
        Assert.Equal(NineAm.AddMinutes(15), repository.Row.NextDueUtc);
    }

    [Fact]
    public void App_routes_the_launching_activation_through_the_composed_router()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var composition = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs"));

        Assert.Contains("notificationRouter.HandleActivationAsync(notificationActivation", app, StringComparison.Ordinal);
        Assert.Contains("activation?.Destination", app, StringComparison.Ordinal);
        Assert.Contains("NotificationRouter = composedNotificationRouter", composition, StringComparison.Ordinal);
        Assert.Contains("ReminderActions = reminderToastActions", composition, StringComparison.Ordinal);
        Assert.Contains(".OccurrenceDelivered +=", composition, StringComparison.Ordinal);
        Assert.Contains(
            $"`{ReminderToastActions.PageOperation}`",
            File.ReadAllText(Path.Combine(root, "AGENTS.md")),
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class SingleRowRepository(Reminder row) : IReminderRepository
    {
        public Reminder Row { get; private set; } = row;

        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([Row]);

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            if (reminder != Row) return Task.FromResult(false);
            Row = Row with { NextDueUtc = nextDueUtc, SnoozedUntilUtc = null };
            return Task.FromResult(true);
        }
    }
}
