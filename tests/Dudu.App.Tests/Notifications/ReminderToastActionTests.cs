using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Notifications;

/// <summary>
/// Regression coverage for reminder toast buttons and toast body clicks. Done and
/// Snooze used to only open the Reminders page and drop the toast (the reminder was
/// never acknowledged nor brought back), and a click on the toast body did nothing.
/// The schedule tests run the real ReminderEngine against a repository with the same
/// load/compare-and-set semantics as the SQLite one (whose native library cannot load
/// in this win-x64 test host off Windows), because the toast only ever appears after
/// the engine has already advanced the reminder row.
/// </summary>
public sealed class ReminderToastActionTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryReminderRepository _repository = new();
    private readonly Clock _clock = new(NineAm);
    private readonly RecordingDueSink _due = new();
    private readonly RecordingErrorReporter _reporter = new();
    private readonly List<string> _dismissedToasts = new();
    private readonly List<string> _discardedHeld = new();
    private readonly List<PetEvent> _petEvents = new();

    [Fact]
    public async Task Snooze_brings_the_announced_occurrence_back_in_fifteen_minutes()
    {
        await SaveAsync(Daily("toast-daily", NineAm));
        var engine = new ReminderEngine(_clock, _repository, _due);
        await engine.TickAsync(Token);
        Assert.Equal([NineAm], _due.For("toast-daily"));

        _clock.UtcNow = NineAm.AddMinutes(2);
        Assert.True(await Actions().SnoozeAsync("toast-daily", Token));

        Assert.Equal(NineAm.AddMinutes(17), (await LoadAsync("toast-daily")).NextDueUtc);
        Assert.Equal(["toast-daily"], _dismissedToasts);
        Assert.Equal(["toast-daily"], _discardedHeld);
        Assert.Contains(_petEvents, e => e is PetEvent.Dismissed { ItemId: "toast-daily" });

        _clock.UtcNow = NineAm.AddMinutes(16);
        await engine.TickAsync(Token);
        Assert.Single(_due.For("toast-daily"));

        _clock.UtcNow = NineAm.AddMinutes(17);
        await engine.TickAsync(Token);
        Assert.Equal([NineAm, NineAm.AddMinutes(17)], _due.For("toast-daily"));
        // The daily schedule itself is untouched: tomorrow is still 09:00.
        Assert.Equal(NineAm.AddDays(1), (await LoadAsync("toast-daily")).NextDueUtc);
        Assert.Empty(_reporter.Operations);
    }

    [Fact]
    public async Task Done_acknowledges_without_skipping_the_next_occurrence()
    {
        await SaveAsync(Hourly("toast-hourly", NineAm));
        var engine = new ReminderEngine(_clock, _repository, _due);
        await engine.TickAsync(Token);
        var afterDelivery = await LoadAsync("toast-hourly");
        Assert.Equal(NineAm.AddHours(1), afterDelivery.NextDueUtc);

        _clock.UtcNow = NineAm.AddMinutes(5);
        Assert.True(await Actions().CompleteAsync("toast-hourly", Token));

        // Completing again through the scheduler would have moved this to 11:00.
        Assert.Equal(afterDelivery, await LoadAsync("toast-hourly"));
        Assert.Equal(["toast-hourly"], _dismissedToasts);
        Assert.Equal(["toast-hourly"], _discardedHeld);
        Assert.Contains(_petEvents, e => e is PetEvent.Dismissed { ItemId: "toast-hourly" });

        _clock.UtcNow = NineAm.AddHours(1);
        await engine.TickAsync(Token);
        Assert.Equal([NineAm, NineAm.AddHours(1)], _due.For("toast-hourly"));
    }

    [Fact]
    public async Task Snooze_leaves_the_schedule_alone_when_the_next_occurrence_comes_sooner()
    {
        var reminder = Hourly("toast-often", NineAm.AddMinutes(10)) with
        {
            Rule = new RecurrenceRule.Interval(TimeSpan.FromMinutes(10)),
        };
        await SaveAsync(reminder);

        Assert.True(await Actions().SnoozeAsync("toast-often", Token));

        Assert.Equal(reminder, await LoadAsync("toast-often"));
        Assert.Equal(["toast-often"], _dismissedToasts);
    }

    [Fact]
    public async Task Snooze_never_brings_back_a_reminder_that_was_switched_off_or_deleted()
    {
        var off = Daily("toast-off", NineAm.AddDays(1)) with { Enabled = false };
        await SaveAsync(off);

        Assert.True(await Actions().SnoozeAsync("toast-off", Token));
        Assert.True(await Actions().SnoozeAsync("toast-gone", Token));

        Assert.Equal(off, await LoadAsync("toast-off"));
        Assert.Equal(["toast-off", "toast-gone"], _dismissedToasts);
        Assert.Empty(_reporter.Operations);
    }

    [Fact]
    public async Task Snooze_that_loses_a_race_retries_against_the_row_that_won()
    {
        await SaveAsync(Daily("toast-race", NineAm.AddDays(1)));
        _clock.UtcNow = NineAm.AddMinutes(2);
        // A settings edit renames it between the read and the write.
        _repository.BeforeNextCompareAndSet = () =>
            _repository.SaveAsync(Daily("toast-race", NineAm.AddDays(1)) with { Title = "Stretch more" }).Wait();

        Assert.True(await Actions().SnoozeAsync("toast-race", Token));

        var stored = await LoadAsync("toast-race");
        Assert.Equal("Stretch more", stored.Title);
        Assert.Equal(NineAm.AddMinutes(17), stored.NextDueUtc);
        Assert.Empty(_reporter.Operations);
    }

    [Fact]
    public async Task Snooze_that_keeps_losing_compare_and_set_reports_without_reminder_text_and_keeps_the_toast()
    {
        var repository = new AlwaysConflictingRepository(
            Daily("toast-busy", NineAm.AddDays(1)) with { Title = "secret title", Details = "secret details" });
        var actions = new ReminderToastActions(
            _clock,
            repository,
            (id, _) => { _dismissedToasts.Add(id); return Task.CompletedTask; },
            (id, _) => { _discardedHeld.Add(id); return Task.CompletedTask; },
            errorReporter: _reporter);

        Assert.False(await actions.SnoozeAsync("toast-busy", Token));

        Assert.Equal(ReminderToastActions.MaxSnoozeAttempts, repository.Attempts);
        var (operation, exception) = Assert.Single(_reporter.Reports);
        Assert.Equal(ReminderToastActions.Operation, operation);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_dismissedToasts);
        Assert.Empty(_discardedHeld);
    }

    [Fact]
    public async Task A_failed_cleanup_step_is_reported_and_does_not_stop_the_others()
    {
        await SaveAsync(Daily("toast-cleanup", NineAm.AddDays(1)));
        var actions = new ReminderToastActions(
            _clock,
            _repository,
            (_, _) => throw new InvalidOperationException("toast gone"),
            (id, _) => { _discardedHeld.Add(id); return Task.CompletedTask; },
            (_, _) => throw new InvalidOperationException("pet busy"),
            _reporter);

        Assert.True(await actions.CompleteAsync("toast-cleanup", Token));

        Assert.Equal(["toast-cleanup"], _discardedHeld);
        Assert.Equal(
            [ReminderToastActions.Operation, ReminderToastActions.Operation],
            _reporter.Operations);
    }

    // ---- toast click routing ------------------------------------------------

    [Fact]
    public async Task Reminder_toast_body_click_opens_the_reminders_page()
    {
        var sink = new RecordingNotificationSink();
        await new AppNotificationService(sink).ShowReminderAsync("reminder-1", "Stretch", Token);
        var body = Assert.Single(sink.Items).ActivationArguments;
        Assert.Equal("action=open-reminder&reminderId=reminder-1", body);

        var router = Router(actions: null, out var navigated, out var dismissed);
        await router.HandleAsync(Map(body!), Token);

        Assert.Equal(["reminders"], navigated);
        Assert.Empty(dismissed);
    }

    [Fact]
    public async Task Note_toast_body_click_opens_love_notes()
    {
        var router = Router(actions: null, out var navigated, out _);

        await router.HandleAsync(
            Map("action=open-note&messageId=11111111-1111-4111-8111-111111111111"),
            Token);

        Assert.Equal(["notes"], navigated);
    }

    [Fact]
    public async Task Done_and_snooze_buttons_act_in_the_background_without_opening_settings()
    {
        await SaveAsync(Daily("toast-button", NineAm));
        await new ReminderEngine(_clock, _repository, _due).TickAsync(Token);
        _clock.UtcNow = NineAm.AddMinutes(1);
        var router = Router(Actions(), out var navigated, out _);

        await router.HandleAsync(Map("action=reminder-snooze&reminderId=toast-button"), Token);
        Assert.Equal(NineAm.AddMinutes(16), (await LoadAsync("toast-button")).NextDueUtc);

        await router.HandleAsync(Map("action=reminder-done&reminderId=toast-button"), Token);

        Assert.Empty(navigated);
        Assert.Equal(["toast-button", "toast-button"], _dismissedToasts);
    }

    [Fact]
    public async Task A_snooze_that_could_not_be_saved_opens_the_reminders_page_instead()
    {
        var actions = new ReminderToastActions(
            _clock,
            new AlwaysConflictingRepository(Daily("toast-busy", NineAm.AddDays(1))),
            (id, _) => { _dismissedToasts.Add(id); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask,
            errorReporter: _reporter);
        var router = Router(actions, out var navigated, out var dismissed);

        await router.HandleAsync(Map("action=reminder-snooze&reminderId=toast-busy"), Token);

        Assert.Equal(["reminders"], navigated);
        Assert.Empty(dismissed);
        Assert.Empty(_dismissedToasts);
    }

    [Fact]
    public async Task Before_the_features_are_ready_done_still_drops_the_toast_and_opens_reminders()
    {
        var router = Router(actions: null, out var navigated, out var dismissed);

        await router.HandleAsync(Map("action=reminder-done&reminderId=reminder-1"), Token);

        Assert.Equal(["reminder-1"], dismissed);
        Assert.Equal(["reminders"], navigated);
    }

    [Fact]
    public async Task Unknown_or_malformed_clicks_are_ignored_and_navigation_failures_are_reported()
    {
        var router = Router(actions: null, out var navigated, out _);
        await router.HandleAsync(Map("action=launch-rockets"), Token);
        await router.HandleAsync(null, Token);
        Assert.Empty(navigated);

        var failing = new NotificationInvocationRouter(
            (_, _) => Task.FromException(new InvalidOperationException("no ui")),
            () => null,
            (_, _) => Task.CompletedTask,
            _reporter);
        await failing.HandleAsync(Map("action=open-reminder&reminderId=reminder-1"), Token);

        Assert.Equal([NotificationInvocationRouter.NavigateOperation], _reporter.Operations);
    }

    [Theory]
    [InlineData("action=open-reminder&reminderId=reminder-1", NotificationActivationAction.OpenReminder, "reminders")]
    [InlineData("action=reminder-done&reminderId=reminder-1", NotificationActivationAction.ReminderDone, "reminders")]
    [InlineData("action=reminder-snooze&reminderId=reminder-1", NotificationActivationAction.ReminderSnooze, "reminders")]
    [InlineData("action=open-note&messageId=11111111-1111-4111-8111-111111111111", NotificationActivationAction.OpenNote, "notes")]
    public void Every_activation_maps_to_its_page(string arguments, NotificationActivationAction action, string destination)
    {
        var activation = NotificationActivation.TryParse(arguments);

        Assert.NotNull(activation);
        Assert.Equal(action, activation!.Action);
        Assert.Equal(destination, activation.Destination);
    }

    [Fact]
    public void Open_reminder_rejects_an_unsafe_id()
    {
        Assert.Null(NotificationActivation.TryParse("action=open-reminder&reminderId=../evil"));
        Assert.Null(NotificationActivation.TryParse("action=open-reminder"));
    }

    // ---- helpers ------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ReminderToastActions Actions() => new(
        _clock,
        _repository,
        (id, _) => { _dismissedToasts.Add(id); return Task.CompletedTask; },
        (id, _) => { _discardedHeld.Add(id); return Task.CompletedTask; },
        (petEvent, _) => { _petEvents.Add(petEvent); return Task.CompletedTask; },
        _reporter);

    private NotificationInvocationRouter Router(
        ReminderToastActions? actions,
        out List<string> navigated,
        out List<string> dismissed)
    {
        var navigatedTo = new List<string>();
        var dismissedIds = new List<string>();
        navigated = navigatedTo;
        dismissed = dismissedIds;
        return new NotificationInvocationRouter(
            (destination, _) => { navigatedTo.Add(destination); return Task.CompletedTask; },
            () => actions,
            (id, _) => { dismissedIds.Add(id); return Task.CompletedTask; },
            _reporter);
    }

    private static Dictionary<string, string> Map(string arguments) =>
        NotificationArguments.Parse(arguments).ToDictionary(pair => pair.Key, pair => pair.Value);

    private Task SaveAsync(Reminder reminder) => _repository.SaveAsync(reminder, Token);

    private async Task<Reminder> LoadAsync(string id) =>
        (await _repository.ListAsync(Token)).Single(item => item.Id == id);

    private static Reminder Daily(string id, DateTimeOffset nextDue) => new(
        id,
        "Stretch",
        null,
        true,
        new RecurrenceRule.Daily(new TimeOnly(9, 0)),
        "UTC",
        QuietHoursBehavior.DeliverImmediately,
        MissedOccurrencePolicy.LatestOnly,
        nextDue);

    private static Reminder Hourly(string id, DateTimeOffset nextDue) => new(
        id,
        "Drink water",
        null,
        true,
        new RecurrenceRule.Interval(TimeSpan.FromHours(1)),
        "UTC",
        QuietHoursBehavior.DeliverImmediately,
        MissedOccurrencePolicy.LatestOnly,
        nextDue);

    private sealed class Clock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RecordingDueSink : IReminderDueSink
    {
        private readonly List<ReminderOccurrence> _occurrences = new();

        public DateTimeOffset[] For(string id) =>
            _occurrences.Where(item => item.ReminderId == id).Select(item => item.DueUtc).ToArray();

        public Task NotifyAsync(ReminderOccurrence occurrence, CancellationToken cancellationToken)
        {
            _occurrences.Add(occurrence);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryReminderRepository : IReminderRepository, IReminderWriter
    {
        private readonly Dictionary<string, Reminder> _rows = new(StringComparer.Ordinal);

        /// <summary>Runs once, just before the next compare-and-set, to simulate
        /// a concurrent writer winning the race.</summary>
        public Action? BeforeNextCompareAndSet { get; set; }

        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(_rows.Values.ToArray());

        // Mirrors ReminderRepository.LoadDueAsync's WHERE clause.
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(_rows.Values
                .Where(item => item.Enabled
                    && item.NextDueUtc <= utcNow
                    && (item.SnoozedUntilUtc is null || item.SnoozedUntilUtc <= utcNow))
                .OrderBy(item => item.NextDueUtc)
                .ToArray());

        // Mirrors ReminderRepository's compare-and-set: the whole expected row must
        // still match; the update sets next_due_utc and clears snoozed_until_utc.
        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            var race = BeforeNextCompareAndSet;
            BeforeNextCompareAndSet = null;
            race?.Invoke();
            if (!_rows.TryGetValue(reminder.Id, out var stored) || stored != reminder)
            {
                return Task.FromResult(false);
            }

            _rows[reminder.Id] = stored with { NextDueUtc = nextDueUtc, SnoozedUntilUtc = null };
            return Task.FromResult(true);
        }

        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            _rows[reminder.Id] = reminder;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysConflictingRepository(Reminder reminder) : IReminderRepository
    {
        public int Attempts { get; private set; }

        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([reminder]);

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder expected,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(false);
        }
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = new();

        public string[] Operations => Reports.Select(report => report.Operation).ToArray();

        public void Report(string operation, Exception exception) => Reports.Add((operation, exception));
    }
}
