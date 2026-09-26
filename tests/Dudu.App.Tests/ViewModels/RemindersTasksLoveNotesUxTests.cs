using System.ComponentModel;
using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>UI/UX regressions for the Reminders, Tasks &amp; Focus and Love Notes
/// pages. Uses its own fixture so it can model the production stores'
/// compare-and-set semantics and freshly reloaded rows.</summary>
public sealed class RemindersTasksLoveNotesUxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- reminders

    [Fact]
    public void Selecting_a_reminder_loads_its_schedule_before_announcing_the_selection()
    {
        // The page mirrors its unbound schedule/time/weekday controls when
        // SelectedReminder changes; the fields used to be copied only AFTER
        // that notification, so the combo box showed the previous reminder's
        // schedule and a save rewrote this one with it.
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);
        var daily = fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30)));
        (ReminderScheduleKind Kind, TimeOnly Time)? observed = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemindersViewModel.SelectedReminder))
            {
                observed = (viewModel.ScheduleKind, viewModel.LocalTime);
            }
        };

        viewModel.SelectedReminder = daily;

        Assert.Equal((ReminderScheduleKind.Daily, new TimeOnly(21, 30)), observed);
    }

    [Fact]
    public void Selecting_a_reminder_does_not_inherit_the_previous_reminders_weekdays_or_interval()
    {
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);
        viewModel.SelectedReminder = fixture.MakeReminder(
            "r-weekend",
            "brunch",
            new RecurrenceRule.SelectedWeekdays(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, new TimeOnly(11, 0)));
        viewModel.SelectedReminder = fixture.MakeReminder(
            "r-interval",
            "blink",
            new RecurrenceRule.Interval(TimeSpan.FromMinutes(20)));
        viewModel.SelectedReminder = fixture.MakeReminder(
            "r-daily",
            "water",
            new RecurrenceRule.Daily(new TimeOnly(8, 15)));

        Assert.Equal(ReminderScheduleKind.Daily, viewModel.ScheduleKind);
        Assert.Equal(60, viewModel.IntervalMinutes);
        Assert.Equal(
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            viewModel.SelectedWeekdays.OrderBy(day => day).ToArray());
    }

    [Fact]
    public void Selecting_a_once_reminder_shows_its_own_due_time_in_the_editor()
    {
        // A once reminder's time lives only in NextDueUtc; the editor used to
        // keep whatever time the previous selection had, so re-saving moved it.
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);
        viewModel.LocalTime = new TimeOnly(6, 0);

        viewModel.SelectedReminder = fixture.MakeReminder("r-once", "call mum", new RecurrenceRule.Once())
            with { NextDueUtc = DateTimeOffset.Parse("2026-09-12T19:45:00Z") };

        Assert.Equal(ReminderScheduleKind.Once, viewModel.ScheduleKind);
        Assert.Equal(new TimeOnly(19, 45), viewModel.LocalTime);
    }

    [Fact]
    public void Deselecting_a_reminder_clears_the_editor_so_save_cannot_duplicate_it()
    {
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);
        viewModel.SelectedReminder = fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30)));

        viewModel.SelectedReminder = null;

        Assert.Equal(string.Empty, viewModel.Title);
        Assert.Equal(ReminderScheduleKind.Once, viewModel.ScheduleKind);
        Assert.Equal(new TimeOnly(9, 0), viewModel.LocalTime);
    }

    [Fact]
    public async Task Saving_a_brand_new_reminder_still_tells_the_page_to_reset_its_schedule_controls()
    {
        // Nothing was selected, so clearing the selection after save raised no
        // notification: the page kept showing "daily 21:00" while the view model
        // had reset to "once 09:00", and the next save silently used the latter.
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            Title = "stretch",
            ScheduleKind = ReminderScheduleKind.Daily,
            LocalTime = new TimeOnly(21, 0),
        };
        (ReminderScheduleKind Kind, TimeOnly Time)? observed = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemindersViewModel.SelectedReminder))
            {
                observed = (viewModel.ScheduleKind, viewModel.LocalTime);
            }
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal((ReminderScheduleKind.Once, new TimeOnly(9, 0)), observed);
    }

    [Fact]
    public async Task Opening_the_page_does_not_auto_select_so_a_new_title_creates_a_new_reminder()
    {
        // Refresh used to auto-select the first reminder, putting the form in
        // edit mode: typing a "new" reminder overwrote an existing one.
        var fixture = Fixture.Create();
        var existing = fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30)));
        fixture.Reminders.Items.Add(existing);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.RefreshAsync(Ct);
        Assert.Null(viewModel.SelectedReminder);
        Assert.Equal(string.Empty, viewModel.Title);

        viewModel.Title = "drink water";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, fixture.Reminders.Items.Count);
        Assert.Contains(fixture.Reminders.Items, item => item.Id == "r-daily" && item.Title == "stretch");
        Assert.Contains(fixture.Reminders.Items, item => item.Title == "drink water");
    }

    [Fact]
    public async Task Refresh_repoints_the_selection_at_the_fresh_row_and_keeps_unsaved_edits()
    {
        var fixture = Fixture.Create();
        var original = fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30)));
        fixture.Reminders.Items.Add(original);
        var viewModel = new RemindersViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedReminder = viewModel.Reminders.Single();
        viewModel.Title = "stretch longer";

        // The background engine advanced the row meanwhile.
        var advanced = original with { NextDueUtc = original.NextDueUtc!.Value.AddDays(1) };
        fixture.Reminders.Items[0] = advanced;
        await viewModel.RefreshAsync(Ct);

        Assert.Equal(advanced, viewModel.SelectedReminder);
        Assert.Equal("stretch longer", viewModel.Title);

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.SelectedReminder);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Completing_the_same_reminder_twice_acts_on_the_fresh_row()
    {
        // Complete/snooze replaced the row in the list but left SelectedReminder
        // (the buttons' CommandParameter) on the stale copy, so the second
        // action failed the store's compare-and-set: "reminder changed".
        var fixture = Fixture.Create();
        fixture.Reminders.Items.Add(fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30))));
        var viewModel = new RemindersViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedReminder = viewModel.Reminders.Single();

        await viewModel.SnoozeCommand.ExecuteAsync(viewModel.SelectedReminder);
        Assert.Null(viewModel.ErrorMessage);
        Assert.NotNull(viewModel.SelectedReminder!.SnoozedUntilUtc);

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.SelectedReminder);
        Assert.Null(viewModel.ErrorMessage);
        var firstNext = viewModel.SelectedReminder!.NextDueUtc;

        await viewModel.CompleteCommand.ExecuteAsync(viewModel.SelectedReminder);
        Assert.Null(viewModel.ErrorMessage);
        Assert.True(viewModel.SelectedReminder!.NextDueUtc > firstNext);
        Assert.Same(viewModel.Reminders.Single(), viewModel.SelectedReminder);
    }

    [Fact]
    public async Task A_selected_weekdays_reminder_with_no_days_is_rejected_instead_of_becoming_monday()
    {
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            Title = "yoga",
            ScheduleKind = ReminderScheduleKind.SelectedWeekdays,
            SelectedWeekdays = new HashSet<DayOfWeek>(),
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("pick at least one day first", viewModel.ErrorMessage);
        Assert.Empty(fixture.Reminders.Items);
        Assert.Equal("yoga", viewModel.Title);
    }

    [Fact]
    public async Task A_new_reminder_uses_the_clocks_local_time_zone()
    {
        var singapore = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
        var fixture = Fixture.Create(zone: singapore);
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            Title = "water",
            ScheduleKind = ReminderScheduleKind.Daily,
            LocalTime = new TimeOnly(21, 0),
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(fixture.Reminders.Items);
        Assert.Equal(singapore.Id, saved.LocalTimeZoneId);
        // Clock is 2026-09-12T10:00Z = 18:00 in Singapore, so 21:00 local today.
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T13:00:00Z"), saved.NextDueUtc);
    }

    [Fact]
    public async Task A_daily_time_inside_a_spring_forward_gap_saves_at_the_first_valid_minute()
    {
        // ConvertTimeToUtc threw on the skipped hour, surfacing raw framework
        // text instead of saving.
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var fixture = Fixture.Create(now: "2027-03-14T05:00:00Z", zone: newYork); // 00:00 EST, DST starts 02:00
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            Title = "night owl",
            ScheduleKind = ReminderScheduleKind.Daily,
            LocalTime = new TimeOnly(2, 30),
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        var saved = Assert.Single(fixture.Reminders.Items);
        Assert.Equal(DateTimeOffset.Parse("2027-03-14T07:00:00Z"), saved.NextDueUtc); // 03:00 EDT
    }

    [Fact]
    public async Task Deleting_a_reminder_requires_confirmation_then_removes_it_everywhere()
    {
        var dismissed = new List<string>();
        var discarded = new List<string>();
        var fixture = Fixture.Create(
            dismissReminderNotificationAsync: (id, _) => { dismissed.Add(id); return Task.CompletedTask; },
            discardHeldReminderAsync: (id, _) => { discarded.Add(id); return Task.CompletedTask; });
        fixture.Reminders.Items.Add(fixture.MakeReminder("r-daily", "stretch", new RecurrenceRule.Daily(new TimeOnly(21, 30))));
        var viewModel = new RemindersViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedReminder = viewModel.Reminders.Single();

        viewModel.RequestDeleteReminderCommand.Execute(viewModel.SelectedReminder);

        Assert.True(viewModel.IsConfirmingDeleteReminder);
        Assert.Equal("delete \"stretch\" for good? cannot undo", viewModel.DeleteReminderPrompt);
        Assert.Single(fixture.Reminders.Items);

        await viewModel.DeleteReminderCommand.ExecuteAsync(viewModel.PendingDeleteReminder);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Empty(fixture.Reminders.Items);
        Assert.Empty(viewModel.Reminders);
        Assert.True(viewModel.HasNoReminders);
        Assert.False(viewModel.IsConfirmingDeleteReminder);
        Assert.Null(viewModel.SelectedReminder);
        Assert.Equal(string.Empty, viewModel.Title);
        Assert.Equal(["r-daily"], dismissed);
        Assert.Equal(["r-daily"], discarded);
    }

    [Fact]
    public async Task Selecting_another_reminder_or_refreshing_cancels_a_pending_delete()
    {
        var fixture = Fixture.Create();
        fixture.Reminders.Items.Add(fixture.MakeReminder("r-1", "one", new RecurrenceRule.Daily(new TimeOnly(8, 0))));
        fixture.Reminders.Items.Add(fixture.MakeReminder("r-2", "two", new RecurrenceRule.Daily(new TimeOnly(9, 0))));
        var viewModel = new RemindersViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedReminder = viewModel.Reminders[0];
        viewModel.RequestDeleteReminderCommand.Execute(viewModel.SelectedReminder);

        viewModel.SelectedReminder = viewModel.Reminders[1];
        Assert.False(viewModel.IsConfirmingDeleteReminder);

        viewModel.RequestDeleteReminderCommand.Execute(viewModel.SelectedReminder);
        await viewModel.RefreshAsync(Ct);
        Assert.False(viewModel.IsConfirmingDeleteReminder);
        Assert.Equal(2, fixture.Reminders.Items.Count);
    }

    [Fact]
    public async Task Built_in_default_reminders_point_to_their_switch_instead_of_offering_delete()
    {
        var fixture = Fixture.Create();
        var hydration = fixture.MakeReminder("default-hydration", "water", new RecurrenceRule.Interval(TimeSpan.FromHours(1)));
        fixture.Reminders.Items.Add(hydration);
        var viewModel = new RemindersViewModel(fixture.Context);

        viewModel.RequestDeleteReminderCommand.Execute(hydration);

        Assert.False(viewModel.IsConfirmingDeleteReminder);
        Assert.Equal("this one comes from helpful defaults, turn it off there instead", viewModel.ErrorMessage);

        await viewModel.DeleteReminderCommand.ExecuteAsync(hydration);
        Assert.Single(fixture.Reminders.Items);
    }

    [Fact]
    public void Only_the_fields_the_schedule_uses_are_editable()
    {
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);

        Assert.True(viewModel.UsesLocalTime);
        Assert.False(viewModel.UsesWeekdays);
        Assert.False(viewModel.UsesInterval);

        viewModel.ScheduleKind = ReminderScheduleKind.SelectedWeekdays;
        Assert.True(viewModel.UsesLocalTime);
        Assert.True(viewModel.UsesWeekdays);

        viewModel.ScheduleKind = ReminderScheduleKind.Interval;
        Assert.False(viewModel.UsesLocalTime);
        Assert.False(viewModel.UsesWeekdays);
        Assert.True(viewModel.UsesInterval);
    }

    [Fact]
    public void A_switched_off_reminder_says_so_in_the_list_summary()
    {
        var rule = new RecurrenceRule.Daily(new TimeOnly(21, 30));
        Assert.Equal("every day at 9:30 pm", ReminderScheduleSummary.DescribeWithState(rule, enabled: true));
        Assert.Equal("off · every day at 9:30 pm", ReminderScheduleSummary.DescribeWithState(rule, enabled: false));
    }

    [Fact]
    public async Task Reminders_empty_state_follows_the_list()
    {
        var fixture = Fixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);
        Assert.True(viewModel.HasNoReminders);

        fixture.Reminders.Items.Add(fixture.MakeReminder("r-1", "one", new RecurrenceRule.Daily(new TimeOnly(8, 0))));
        await viewModel.RefreshAsync(Ct);

        Assert.False(viewModel.HasNoReminders);
    }

    // ------------------------------------------------------------ tasks & focus

    [Fact]
    public async Task Completing_the_selected_task_clears_the_editor_so_save_cannot_duplicate_it()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "book dinner" };
        await viewModel.SaveTaskCommand.ExecuteAsync(null);
        var task = viewModel.ActiveTasks.Single();
        viewModel.SelectTask(task);
        viewModel.DueUtc = DateTimeOffset.Parse("2026-09-13T10:00:00Z");

        await viewModel.CompleteTaskCommand.ExecuteAsync(task);

        Assert.Null(viewModel.SelectedTask);
        Assert.Equal(string.Empty, viewModel.Title);
        Assert.Null(viewModel.DueUtc);
        Assert.Empty(viewModel.ActiveTasks);
    }

    [Fact]
    public async Task Saving_a_blank_task_reports_friendly_copy()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "   " };

        await viewModel.SaveTaskCommand.ExecuteAsync(null);

        Assert.Equal("aiyo add a title first", viewModel.ErrorMessage);
        Assert.Empty(viewModel.ActiveTasks);
    }

    [Fact]
    public async Task Focus_buttons_are_only_enabled_when_their_action_can_succeed()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);

        AssertFocusButtons(viewModel, start: true, pause: false, resume: false, extend: false, end: false);

        await viewModel.StartFocusCommand.ExecuteAsync(null);
        AssertFocusButtons(viewModel, start: false, pause: true, resume: false, extend: true, end: true);

        await viewModel.PauseFocusCommand.ExecuteAsync(null);
        AssertFocusButtons(viewModel, start: false, pause: false, resume: true, extend: true, end: true);

        await viewModel.ResumeFocusCommand.ExecuteAsync(null);
        AssertFocusButtons(viewModel, start: false, pause: true, resume: false, extend: true, end: true);

        await viewModel.EndFocusCommand.ExecuteAsync(null);
        AssertFocusButtons(viewModel, start: true, pause: false, resume: false, extend: false, end: false);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Focus_button_state_changes_are_announced_so_buttons_refresh()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        var pauseChanged = 0;
        viewModel.PauseFocusCommand.CanExecuteChanged += (_, _) => pauseChanged++;

        await viewModel.StartFocusOrThrowAsync(Ct);

        Assert.True(pauseChanged > 0);
    }

    [Fact]
    public async Task Running_focus_countdown_ticks_down_and_paused_focus_holds_still()
    {
        // The status line used to be computed once per snapshot, so a running
        // session read "25 min left" until she navigated away and back.
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        await viewModel.StartFocusOrThrowAsync(Ct);
        Assert.Equal("focus is running with 25 min left", viewModel.ActiveFocusText);

        var textChanged = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TasksFocusViewModel.ActiveFocusText)) textChanged = true;
        };
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(10).AddSeconds(30);
        viewModel.RefreshFocusCountdown();

        Assert.True(textChanged);
        Assert.Equal("focus is running with 15 min left", viewModel.ActiveFocusText);

        await viewModel.PauseFocusCommand.ExecuteAsync(null);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(7);
        viewModel.RefreshFocusCountdown();
        Assert.Equal("focus is paused with 15 min left", viewModel.ActiveFocusText);
    }

    [Fact]
    public async Task Focus_countdown_never_goes_negative_and_formats_hours()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context)
        {
            SelectedDurationMinutes = 0,
            CustomDurationMinutes = 90,
        };
        await viewModel.StartFocusOrThrowAsync(Ct);
        Assert.Equal("focus is running with 1 hr 30 min left", viewModel.ActiveFocusText);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(30);
        viewModel.RefreshFocusCountdown();
        Assert.Equal("focus is running with 1 hr left", viewModel.ActiveFocusText);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(5);
        viewModel.RefreshFocusCountdown();
        Assert.Equal(TimeSpan.Zero, viewModel.ActiveFocusRemaining);
        Assert.Equal("focus is running with 0 min left", viewModel.ActiveFocusText);
    }

    [Fact]
    public async Task Focus_status_copy_is_plain_words_for_every_state()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        Assert.Equal("no focus running", viewModel.ActiveFocusText);

        await viewModel.StartFocusOrThrowAsync(Ct);
        await viewModel.EndFocusCommand.ExecuteAsync(null);

        // Used to read "focus is endedearly with 0 minutes remaining".
        Assert.Equal("last focus session ended early", viewModel.ActiveFocusText);
    }

    [Fact]
    public void Custom_minutes_are_only_editable_for_the_custom_preset()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        Assert.False(viewModel.IsCustomDuration);

        viewModel.SelectedDurationMinutes = 0;
        Assert.True(viewModel.IsCustomDuration);

        viewModel.SelectedDurationMinutes = 45;
        Assert.False(viewModel.IsCustomDuration);
    }

    [Fact]
    public async Task Task_and_focus_empty_states_follow_their_lists()
    {
        var fixture = Fixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        Assert.True(viewModel.HasNoActiveTasks);
        Assert.True(viewModel.HasNoCompletedTasks);
        Assert.True(viewModel.HasNoFocusHistory);

        viewModel.Title = "tidy desk";
        await viewModel.SaveTaskCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasNoActiveTasks);

        await viewModel.CompleteTaskCommand.ExecuteAsync(viewModel.ActiveTasks.Single());
        Assert.True(viewModel.HasNoActiveTasks);
        Assert.False(viewModel.HasNoCompletedTasks);

        await viewModel.StartFocusOrThrowAsync(Ct);
        await viewModel.EndFocusCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasNoFocusHistory);
    }

    // --------------------------------------------------------------- love notes

    [Fact]
    public async Task Saving_an_opened_note_after_a_refresh_removes_it_from_the_incoming_list()
    {
        // RemoteEnvelope carries byte[] fields, so a reloaded row never equals
        // the instance that was revealed; Remove(envelope) silently failed and
        // the saved note stayed listed as unopened.
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedRemoteEnvelope = viewModel.PendingRemoteNotes.Single();
        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(viewModel.SelectedRemoteEnvelope);
        await viewModel.RefreshAsync(Ct);

        await viewModel.SaveOpenedNoteCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Empty(viewModel.PendingRemoteNotes);
        Assert.Equal(0, viewModel.UnopenedRemoteNoteCount);
    }

    [Fact]
    public async Task Refresh_keeps_the_incoming_selection_on_the_fresh_row()
    {
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        viewModel.SelectedRemoteEnvelope = viewModel.PendingRemoteNotes.Single();

        await viewModel.RefreshAsync(Ct);

        Assert.Same(viewModel.PendingRemoteNotes.Single(), viewModel.SelectedRemoteEnvelope);
        Assert.True(viewModel.CanRevealRemoteNote);
    }

    [Fact]
    public async Task Refresh_drops_an_opened_note_that_was_already_consumed_elsewhere()
    {
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(viewModel.PendingRemoteNotes.Single());
        Assert.True(viewModel.CanSaveOpenedNote);

        fixture.RemoteNotes.Pending.Clear(); // saved from the pet meanwhile
        await viewModel.RefreshAsync(Ct);

        Assert.False(viewModel.CanSaveOpenedNote);
        Assert.False(viewModel.HasOpenedRemoteNote);
        Assert.Null(viewModel.OpenedRemoteNoteText);
    }

    [Fact]
    public async Task Save_opened_note_is_only_available_for_a_revealed_incoming_note()
    {
        var fixture = Fixture.Create();
        fixture.LocalNotes.Notes.Add(new LocalLoveNote("n-1", "you got this"));
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        Assert.False(viewModel.CanSaveOpenedNote);

        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(viewModel.PendingRemoteNotes.Single());
        Assert.True(viewModel.CanSaveOpenedNote);

        await viewModel.ShowLocalNoteCommand.ExecuteAsync(null);
        Assert.Equal("you got this", viewModel.OpenedRemoteNoteText);
        Assert.False(viewModel.CanSaveOpenedNote);
    }

    [Fact]
    public async Task Let_dudu_choose_with_an_empty_jar_explains_what_to_do()
    {
        var fixture = Fixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context);

        await viewModel.ShowLocalNoteCommand.ExecuteAsync(null);

        // Used to read "cannot add a local note first".
        Assert.Equal("add a note to the jar or turn one on first", viewModel.ErrorMessage);
    }

    [Fact]
    public void Incoming_rows_are_distinguishable_by_their_local_arrival_time()
    {
        var first = DateTimeOffset.Parse("2026-09-12T09:00:00Z");
        var second = DateTimeOffset.Parse("2026-09-12T11:30:00Z");

        var firstText = LoveNoteDisplay.DescribeEnvelope(first);
        Assert.StartsWith("encrypted note received ", firstText);
        Assert.EndsWith(first.ToLocalTime().ToString("g"), firstText);
        Assert.NotEqual(firstText, LoveNoteDisplay.DescribeEnvelope(second));
    }

    [Fact]
    public async Task Note_jar_empty_state_follows_the_list()
    {
        var fixture = Fixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context);
        Assert.True(viewModel.HasNoLocalNotes);

        viewModel.DraftText = "proud of you";
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasNoLocalNotes);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertFocusButtons(
        TasksFocusViewModel viewModel,
        bool start,
        bool pause,
        bool resume,
        bool extend,
        bool end)
    {
        Assert.Equal(start, viewModel.StartFocusCommand.CanExecute(null));
        Assert.Equal(pause, viewModel.PauseFocusCommand.CanExecute(null));
        Assert.Equal(resume, viewModel.ResumeFocusCommand.CanExecute(null));
        Assert.Equal(extend, viewModel.ExtendFocusCommand.CanExecute(null));
        Assert.Equal(end, viewModel.EndFocusCommand.CanExecute(null));
    }

    private sealed class Fixture
    {
        private Fixture(
            FakeClock clock,
            FakeReminderRepository reminders,
            FakeLocalNoteRepository localNotes,
            FakeRemoteEnvelopeRepository remoteNotes,
            CompanionFeatureContext context)
        {
            Clock = clock;
            Reminders = reminders;
            LocalNotes = localNotes;
            RemoteNotes = remoteNotes;
            Context = context;
        }

        public FakeClock Clock { get; }
        public FakeReminderRepository Reminders { get; }
        public FakeLocalNoteRepository LocalNotes { get; }
        public FakeRemoteEnvelopeRepository RemoteNotes { get; }
        public CompanionFeatureContext Context { get; }

        public Reminder MakeReminder(string id, string title, RecurrenceRule rule) => new(
            id,
            title,
            null,
            true,
            rule,
            Clock.LocalTimeZone.Id,
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.LatestOnly,
            Clock.UtcNow.AddHours(2));

        public static Fixture Create(
            string now = "2026-09-12T10:00:00Z",
            TimeZoneInfo? zone = null,
            Func<string, CancellationToken, Task>? dismissReminderNotificationAsync = null,
            Func<string, CancellationToken, Task>? discardHeldReminderAsync = null)
        {
            var clock = new FakeClock(DateTimeOffset.Parse(now), zone ?? TimeZoneInfo.Utc);
            var preferences = new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15));
            var preferenceRepository = new FakePreferencesRepository();
            var reminders = new FakeReminderRepository();
            var tasks = new FakeTaskRepository();
            var focusSessions = new FakeFocusRepository();
            var localNotes = new FakeLocalNoteRepository();
            var remoteNotes = new FakeRemoteEnvelopeRepository();
            var checkIns = new FakeCheckInRepository();
            var context = new CompanionFeatureContext(
                clock,
                new PreferenceMutationCoordinator(preferences, preferenceRepository),
                new FakeProfileRepository(),
                new FakePlacementRepository(),
                reminders,
                reminders,
                tasks,
                focusSessions,
                localNotes,
                remoteNotes,
                new FakeCountdownRepository(),
                checkIns,
                new CheckInService(checkIns, clock),
                new TaskService(tasks, clock),
                new FocusService(focusSessions, clock, tasks),
                new LocalNoteSelector(localNotes, clock, new FixedRandom(), preferences),
                new FakePairing(),
                new FakeFeatureTransactions(localNotes, remoteNotes),
                PetStateMachine.CreateIdle(),
                revealRemoteNoteAsync: (_, _) => Task.FromResult(new RevealedRemoteNote("you can do it", "none")),
                dismissReminderNotificationAsync: dismissReminderNotificationAsync,
                discardHeldReminderAsync: discardHeldReminderAsync);
            return new Fixture(clock, reminders, localNotes, remoteNotes, context);
        }
    }

    private sealed class FakeClock(DateTimeOffset utcNow, TimeZoneInfo zone) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public TimeZoneInfo LocalTimeZone { get; } = zone;
    }

    private sealed class FixedRandom : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    /// <summary>Models ReminderRepository's compare-and-set: completing a stale
    /// copy of a row fails exactly like production.</summary>
    private sealed class FakeReminderRepository : IReminderRepository, IReminderWriter
    {
        public List<Reminder> Items { get; } = [];
        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(Items.ToArray());
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(Items.ToArray());
        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken)
        {
            var index = Items.FindIndex(item => item.Id == reminder.Id);
            if (index < 0 || Items[index] != reminder) return Task.FromResult(false);
            Items[index] = reminder with { NextDueUtc = nextDueUtc, SnoozedUntilUtc = null };
            return Task.FromResult(true);
        }
        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            ReminderScheduler.ValidateForSave(reminder);
            var index = Items.FindIndex(item => item.Id == reminder.Id);
            if (index >= 0) Items[index] = reminder; else Items.Add(reminder);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string reminderId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.Id == reminderId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocalNoteRepository : ILocalNoteRepository
    {
        public List<LocalLoveNote> Notes { get; } = [];
        public Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.ToArray());
        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.Where(note => note.Enabled).ToArray());
        public Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken)
        {
            Notes.RemoveAll(item => item.Id == note.Id);
            Notes.Add(note);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string noteId, CancellationToken cancellationToken) { Notes.RemoveAll(item => item.Id == noteId); return Task.CompletedTask; }
        public Task<int> CountUnsolicitedShownAsync(DateOnly localDate, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> TryRecordShownAsync(string noteId, DateTimeOffset shownUtc, DateOnly localDate, int dailyLimit, bool unsolicited, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>Returns freshly materialized rows (new byte[] instances) on every
    /// list, like the SQLite repository does.</summary>
    private sealed class FakeRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public List<RemoteEnvelope> Pending { get; } = [];
        private static RemoteEnvelope Fresh(RemoteEnvelope envelope) => envelope with { Ciphertext = envelope.Ciphertext.ToArray() };
        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Pending.Where(item => item.MessageId == messageId).Select(Fresh).FirstOrDefault());
        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEnvelope>>(Pending.Select(Fresh).ToArray());
        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) { Pending.Add(envelope); return Task.FromResult(true); }
        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> TryConsumeAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Pending.RemoveAll(item => item.MessageId == messageId) == 1);
        public Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) { Pending.RemoveAll(item => item.MessageId == messageId); return Task.CompletedTask; }
        public Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) { var count = Pending.Count; Pending.Clear(); return Task.FromResult(count); }
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Preferences? Current { get; set; }
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            Current = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<Profile?>(null);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePlacementRepository : IPetPlacementRepository
    {
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.FromResult<PetPlacement?>(null);
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PetPlacement>>([]);
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        private readonly Dictionary<Guid, TaskItem> _tasks = [];
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_tasks.GetValueOrDefault(id));
        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.Values.Where(item => !item.IsCompleted).ToArray());
        public Task<IReadOnlyList<TaskItem>> ListCompletedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.Values.Where(item => item.IsCompleted).ToArray());
        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken) { _tasks[task.Id] = task; return Task.CompletedTask; }
        public Task<bool> TryCompareAndSetAsync(TaskItem expected, TaskItem replacement, CancellationToken cancellationToken)
        {
            if (!_tasks.TryGetValue(expected.Id, out var current) || current != expected) return Task.FromResult(false);
            _tasks[replacement.Id] = replacement;
            return Task.FromResult(true);
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) { _tasks.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeFocusRepository : IFocusSessionRepository
    {
        private readonly Dictionary<Guid, FocusSession> _sessions = [];
        private FocusSession? Active => _sessions.Values.FirstOrDefault(item => item.Status is FocusStatus.Running or FocusStatus.Paused);
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);
        public Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FocusSession>>(_sessions.Values.Where(item => item.Status is not (FocusStatus.Running or FocusStatus.Paused)).ToArray());
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            if (Active is not null) return Task.FromResult(false);
            _sessions[session.Id] = session;
            return Task.FromResult(true);
        }
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken)
        {
            if (!_sessions.TryGetValue(expected.Id, out var current) || current != expected) return Task.FromResult(false);
            _sessions[expected.Id] = replacement;
            return Task.FromResult(true);
        }
        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken) { _sessions[session.Id] = session; return Task.CompletedTask; }
    }

    private sealed class FakeCountdownRepository : ICountdownRepository
    {
        public Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Countdown?>(null);
        public Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Countdown>>([]);
        public Task SaveAsync(Countdown countdown, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCheckInRepository : ICheckInRepository
    {
        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodCheckIn>>([]);
    }

    private sealed class FakePairing : IPairingService
    {
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingAvailability.Offline);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ForgetPairingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeFeatureTransactions(
        FakeLocalNoteRepository localNotes,
        FakeRemoteEnvelopeRepository remoteNotes) : ICompanionFeatureTransactions
    {
        public Task SavePreferencesAndDefaultRemindersAsync(
            Preferences preferences,
            DateTimeOffset nowUtc,
            TimeZoneInfo localTimeZone,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestorePreferencesAndDefaultRemindersAsync(
            Preferences preferences,
            IReadOnlyList<Reminder> previousDefaultReminders,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SaveRemoteNoteAndConsumeEnvelopeAsync(
            LocalLoveNote note,
            string messageId,
            DateTimeOffset processedUtc,
            CancellationToken cancellationToken = default)
        {
            await localNotes.SaveToJarAsync(note, cancellationToken);
            if (!await remoteNotes.TryConsumeAsync(messageId, processedUtc, cancellationToken))
            {
                throw new InvalidOperationException("Remote note is unavailable.");
            }
        }
    }
}
