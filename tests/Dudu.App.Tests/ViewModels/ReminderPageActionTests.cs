using Dudu.App.Notifications;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>
/// Regression coverage for the Reminders page's Done/Snooze, which now share
/// <see cref="ReminderToastActions"/> with the toast buttons:
/// <list type="bullet">
/// <item>Done after the reminder fired used to complete it again through the
/// scheduler, consuming the next, not-yet-due occurrence (an hourly reminder
/// skipped an hour).</item>
/// <item>Snooze only set SnoozedUntilUtc, which the scheduler ignores once
/// the engine advanced NextDueUtc past it, so a snoozed one-time reminder
/// never came back.</item>
/// <item>A toast Done/Snooze while the page was open left the page on a stale
/// row, and the next page action failed with "reminder changed".</item>
/// </list>
/// The real <see cref="ReminderEngine"/> runs against a repository with the
/// SQLite one's load/compare-and-set semantics, with its announcements wired
/// to the actions exactly like the production composition does.
/// </summary>
public sealed class ReminderPageActionTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly CasReminderRepository _repository = new();
    private readonly RecordingDueSink _due = new();
    private readonly List<string> _dismissedToasts = new();
    private readonly List<string> _discardedHeld = new();
    private readonly List<PetEvent> _petEvents = new();
    private readonly SettingsDataPagesFixture _fixture;
    private readonly ReminderToastActions _actions;
    private readonly ReminderEngine _engine;

    public ReminderPageActionTests()
    {
        _fixture = SettingsDataPagesFixture.Create(
            reminders: _repository,
            reminderWriter: _repository,
            dismissReminderNotificationAsync: (id, _) => { _dismissedToasts.Add(id); return Task.CompletedTask; },
            discardHeldReminderAsync: (id, _) => { _discardedHeld.Add(id); return Task.CompletedTask; },
            presentPetAsync: (petEvent, _) => { _petEvents.Add(petEvent); return Task.CompletedTask; });
        _fixture.Clock.UtcNow = NineAm;
        var context = _fixture.Context;
        _actions = new ReminderToastActions(
            context.Clock,
            context.Reminders,
            context.DismissReminderNotificationAsync,
            context.DiscardHeldReminderAsync,
            context.PresentPetAsync);
        _engine = new ReminderEngine(context.Clock, _repository, _due);
        _engine.OccurrenceDelivered += _actions.RecordAnnounced;
    }

    [Fact]
    public async Task Done_after_an_hourly_reminder_fired_does_not_skip_the_next_hour()
    {
        await _repository.SaveAsync(Hourly("page-hourly", NineAm), Token);
        await _engine.TickAsync(Token);
        var afterDelivery = await LoadAsync("page-hourly");
        Assert.Equal(NineAm.AddHours(1), afterDelivery.NextDueUtc);

        var viewModel = await OpenPageAsync();
        _fixture.Clock.UtcNow = NineAm.AddMinutes(5);
        await viewModel.CompleteCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("yayyy done le good job", viewModel.StatusMessage);
        // Completing through the scheduler again would have moved this to 11:00.
        Assert.Equal(afterDelivery, await LoadAsync("page-hourly"));
        Assert.Equal(["page-hourly"], _dismissedToasts);
        Assert.Equal(["page-hourly"], _discardedHeld);
        Assert.Contains(_petEvents, e => e is PetEvent.Dismissed { ItemId: "page-hourly" });
        Assert.False(_actions.IsAwaitingAnswer("page-hourly"));

        _fixture.Clock.UtcNow = NineAm.AddHours(1);
        await _engine.TickAsync(Token);
        Assert.Equal([NineAm, NineAm.AddHours(1)], _due.For("page-hourly"));
    }

    [Fact]
    public async Task Done_before_the_reminder_fired_consumes_the_pending_occurrence()
    {
        // Completing ahead of time (she already stretched at 8:30) must keep
        // today's 9:00 from still firing; tomorrow's stays.
        _fixture.Clock.UtcNow = NineAm.AddMinutes(-30);
        await _repository.SaveAsync(Daily("page-early", NineAm), Token);
        var viewModel = await OpenPageAsync();

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(NineAm.AddDays(1), (await LoadAsync("page-early")).NextDueUtc);
        Assert.Equal(NineAm.AddDays(1), viewModel.Reminders.Single().NextDueUtc);
        _fixture.Clock.UtcNow = NineAm;
        await _engine.TickAsync(Token);
        Assert.Empty(_due.For("page-early"));
    }

    [Fact]
    public async Task Snooze_brings_a_fired_one_time_reminder_back_in_fifteen_minutes()
    {
        await _repository.SaveAsync(Once("page-once", NineAm), Token);
        await _engine.TickAsync(Token);
        Assert.Null((await LoadAsync("page-once")).NextDueUtc);

        var viewModel = await OpenPageAsync();
        _fixture.Clock.UtcNow = NineAm.AddMinutes(2);
        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("otayyy snoozed for 15 min", viewModel.StatusMessage);
        Assert.Equal(NineAm.AddMinutes(17), (await LoadAsync("page-once")).NextDueUtc);
        Assert.Equal(NineAm.AddMinutes(17), viewModel.Reminders.Single().NextDueUtc);
        Assert.Equal(["page-once"], _dismissedToasts);
        Assert.Equal(["page-once"], _discardedHeld);

        _fixture.Clock.UtcNow = NineAm.AddMinutes(17);
        await _engine.TickAsync(Token);
        Assert.Equal([NineAm, NineAm.AddMinutes(17)], _due.For("page-once"));
    }

    [Fact]
    public async Task Snooze_of_a_fired_daily_reminder_matches_the_toast_and_keeps_tomorrow()
    {
        await _repository.SaveAsync(Daily("page-daily", NineAm), Token);
        await _engine.TickAsync(Token);
        var viewModel = await OpenPageAsync();

        _fixture.Clock.UtcNow = NineAm.AddMinutes(2);
        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());
        Assert.Equal(NineAm.AddMinutes(17), (await LoadAsync("page-daily")).NextDueUtc);

        _fixture.Clock.UtcNow = NineAm.AddMinutes(17);
        await _engine.TickAsync(Token);
        Assert.Equal([NineAm, NineAm.AddMinutes(17)], _due.For("page-daily"));
        Assert.Equal(NineAm.AddDays(1), (await LoadAsync("page-daily")).NextDueUtc);
    }

    [Fact]
    public async Task Snooze_of_a_fired_reminder_whose_next_occurrence_comes_sooner_changes_nothing()
    {
        var often = Hourly("page-often", NineAm) with { Rule = new RecurrenceRule.Interval(TimeSpan.FromMinutes(10)) };
        await _repository.SaveAsync(often, Token);
        await _engine.TickAsync(Token);
        var advanced = await LoadAsync("page-often");
        var viewModel = await OpenPageAsync();

        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Equal(advanced, await LoadAsync("page-often"));
        Assert.Equal("oki the next one comes within 15 min anyway", viewModel.StatusMessage);
        Assert.Equal(["page-often"], _dismissedToasts);
    }

    [Fact]
    public async Task Snooze_of_a_reminder_due_within_the_window_postpones_it()
    {
        _fixture.Clock.UtcNow = NineAm.AddMinutes(-5);
        await _repository.SaveAsync(Daily("page-soon", NineAm), Token);
        var viewModel = await OpenPageAsync();

        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Equal(NineAm.AddMinutes(10), (await LoadAsync("page-soon")).NextDueUtc);
        Assert.Equal("otayyy snoozed for 15 min", viewModel.StatusMessage);
        _fixture.Clock.UtcNow = NineAm;
        await _engine.TickAsync(Token);
        Assert.Empty(_due.For("page-soon"));
        _fixture.Clock.UtcNow = NineAm.AddMinutes(10);
        await _engine.TickAsync(Token);
        Assert.Equal([NineAm.AddMinutes(10)], _due.For("page-soon"));
    }

    [Fact]
    public async Task Snooze_of_a_reminder_that_is_not_due_yet_says_so_and_touches_nothing()
    {
        var later = Daily("page-later", NineAm.AddHours(8));
        await _repository.SaveAsync(later, Token);
        var viewModel = await OpenPageAsync();

        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("not due yet, nothing to snooze", viewModel.StatusMessage);
        Assert.Equal(later, await LoadAsync("page-later"));
        Assert.Empty(_dismissedToasts);
        Assert.Empty(_discardedHeld);
    }

    [Fact]
    public async Task Snooze_of_a_switched_off_reminder_reports_it_instead_of_pretending()
    {
        var off = Daily("page-off", NineAm) with { Enabled = false };
        await _repository.SaveAsync(off, Token);
        var viewModel = await OpenPageAsync();

        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Equal("this reminder is off, turn it on to snooze it", viewModel.ErrorMessage);
        Assert.Null(viewModel.StatusMessage);
        Assert.Equal(off, await LoadAsync("page-off"));
    }

    [Fact]
    public async Task A_toast_answer_while_the_page_is_open_refreshes_the_row_and_the_next_page_action_works()
    {
        await _repository.SaveAsync(Daily("page-toast", NineAm), Token);
        await _engine.TickAsync(Token);
        var viewModel = await OpenPageAsync();
        viewModel.SelectedReminder = viewModel.Reminders.Single();
        viewModel.Title = "stretch longer";
        var stale = viewModel.SelectedReminder;
        // What RemindersPage does on ReminderChanged (minus the UI-thread hop).
        _actions.ReminderChanged += id => viewModel.ReloadReminderAsync(id, Token).GetAwaiter().GetResult();

        _fixture.Clock.UtcNow = NineAm.AddMinutes(1);
        Assert.True(await _actions.SnoozeAsync("page-toast", Token));

        var stored = await LoadAsync("page-toast");
        Assert.Equal(NineAm.AddMinutes(16), stored.NextDueUtc);
        Assert.Equal(stored, viewModel.Reminders.Single());
        Assert.Equal(stored, viewModel.SelectedReminder);
        Assert.Equal("stretch longer", viewModel.Title);

        // Even on the copy from before the toast click, Done no longer fails
        // with "reminder changed": the row is re-read by id.
        await viewModel.CompleteCommand.ExecuteAsync(stale);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("yayyy done le good job", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Engine_announcements_and_answers_raise_reminder_changed()
    {
        var changed = new List<string>();
        _actions.ReminderChanged += changed.Add;
        await _repository.SaveAsync(Daily("page-events", NineAm), Token);

        await _engine.TickAsync(Token);
        Assert.Equal(["page-events"], changed);
        Assert.True(_actions.IsAwaitingAnswer("page-events"));

        Assert.True(await _actions.CompleteAsync("page-events", Token));
        Assert.Equal(["page-events", "page-events"], changed);
        Assert.False(_actions.IsAwaitingAnswer("page-events"));
    }

    [Fact]
    public async Task Done_on_a_reminder_deleted_meanwhile_says_so_and_drops_the_row()
    {
        await _repository.SaveAsync(Daily("page-gone", NineAm.AddDays(1)), Token);
        var viewModel = await OpenPageAsync();
        viewModel.SelectedReminder = viewModel.Reminders.Single();
        await _repository.DeleteAsync("page-gone", Token);

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.SelectedReminder);

        Assert.Equal("oh no this reminder is gone", viewModel.ErrorMessage);
        Assert.Empty(viewModel.Reminders);
        Assert.Null(viewModel.SelectedReminder);
    }

    [Fact]
    public async Task Done_that_keeps_losing_compare_and_set_reports_a_conflict()
    {
        await _repository.SaveAsync(Daily("page-busy", NineAm.AddDays(1)), Token);
        var viewModel = await OpenPageAsync();
        _repository.AlwaysConflict = true;

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.Reminders.Single());

        Assert.Equal("oh no reminder changed before saving", viewModel.ErrorMessage);
        Assert.Empty(_dismissedToasts);
    }

    // ---- helpers ------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<RemindersViewModel> OpenPageAsync()
    {
        var viewModel = new RemindersViewModel(_fixture.Context, _actions);
        await viewModel.RefreshAsync(Token);
        return viewModel;
    }

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

    private static Reminder Hourly(string id, DateTimeOffset nextDue) =>
        Daily(id, nextDue) with { Title = "Drink water", Rule = new RecurrenceRule.Interval(TimeSpan.FromHours(1)) };

    private static Reminder Once(string id, DateTimeOffset nextDue) =>
        Daily(id, nextDue) with { Title = "Call mum", Rule = new RecurrenceRule.Once() };

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

    /// <summary>Same load/compare-and-set semantics as ReminderRepository
    /// (whose native SQLite library cannot load in this test host off
    /// Windows): the whole expected row must still match; the update sets
    /// next_due_utc and clears snoozed_until_utc.</summary>
    private sealed class CasReminderRepository : IReminderRepository, IReminderWriter
    {
        private readonly Dictionary<string, Reminder> _rows = new(StringComparer.Ordinal);

        public bool AlwaysConflict { get; set; }

        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(_rows.Values.OrderBy(item => item.Id).ToArray());

        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(_rows.Values
                .Where(item => item.Enabled
                    && item.NextDueUtc <= utcNow
                    && (item.SnoozedUntilUtc is null || item.SnoozedUntilUtc <= utcNow))
                .OrderBy(item => item.NextDueUtc)
                .ToArray());

        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            if (AlwaysConflict || !_rows.TryGetValue(reminder.Id, out var stored) || stored != reminder)
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

        public Task DeleteAsync(string reminderId, CancellationToken cancellationToken = default)
        {
            _rows.Remove(reminderId);
            return Task.CompletedTask;
        }
    }
}
