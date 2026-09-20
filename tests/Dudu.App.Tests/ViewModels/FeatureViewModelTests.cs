using System.ComponentModel;
using System.Reflection;
using Dudu.App.Overlay;
using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
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

public sealed class FeatureViewModelTests
{
    [Fact]
    public async Task Home_remembers_only_yesterdays_latest_explicit_check_in()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new HomeViewModel(fixture.Context);
        await viewModel.RefreshAsync(ct);
        Assert.Contains("fresh check-in", viewModel.YesterdayReflectionText);

        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-11T20:00:00Z");
        await fixture.Context.CheckInService.RecordAsync(MoodChoice.Rough, "earlier", ct);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);
        await fixture.Context.CheckInService.RecordAsync(MoodChoice.Okay, "finished my work", ct);
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-12T10:00:00Z");
        await fixture.Context.CheckInService.RecordAsync(MoodChoice.Great, "today's note", ct);
        await viewModel.RefreshAsync(ct);

        Assert.Contains("yesterday you chose okay", viewModel.YesterdayReflectionText);
        Assert.Contains("finished my work", viewModel.YesterdayReflectionText);
        Assert.DoesNotContain("earlier", viewModel.YesterdayReflectionText);
        Assert.DoesNotContain("today's note", viewModel.YesterdayReflectionText);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(3);
        await viewModel.RefreshAsync(ct);
        Assert.Contains("fresh check-in", viewModel.YesterdayReflectionText);
    }

    [Fact]
    public async Task Home_text_takes_the_saved_recipient_name_instead_of_a_hardcoded_one()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Profiles.Current = new Profile("mei", OnboardingComplete: true);
        fixture.Reminders.Items.Add(new Reminder(
            LocalReminderDefaults.EveningCheckInId,
            LocalReminderDefaults.EveningCheckInDefaultTitle,
            "a little space to reflect. your check-in stays on this device.",
            true,
            new RecurrenceRule.Daily(new TimeOnly(20, 0)),
            "UTC",
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.Skip,
            DateTimeOffset.Parse("2026-09-13T20:00:00Z")));
        await fixture.Context.CheckInService.RecordAsync(MoodChoice.Okay, "finished my work", ct);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(1);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Contains("how was your day, mei?", viewModel.NextReminderText);
        Assert.Equal("how was your day, mei?", viewModel.CheckInSectionHeading);
        Assert.Contains("how does today feel, mei?", viewModel.YesterdayReflectionText);
    }

    [Fact]
    public async Task Home_text_trims_a_saved_recipient_name_with_stray_whitespace()
    {
        // A name saved with stray surrounding whitespace must not leak into Home's
        // copy as an extra space before the name or before the punctuation.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Profiles.Current = new Profile("  mei  ", OnboardingComplete: true);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Equal("how was your day, mei?", viewModel.CheckInSectionHeading);
    }

    [Fact]
    public async Task Home_text_drops_the_name_clause_naturally_when_no_recipient_name_is_saved()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Context.CheckInService.RecordAsync(MoodChoice.Okay, "finished my work", ct);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(1);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Equal("how was your day?", viewModel.CheckInSectionHeading);
        Assert.Contains("how does today feel?", viewModel.YesterdayReflectionText);
    }

    [Fact]
    public async Task Next_countdown_text_names_the_soonest_countdown_and_its_days_remaining()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Anniversary",
            CountdownTargetUtc = fixture.Clock.UtcNow.AddDays(10),
        };
        await viewModel.SaveCountdownAsync(ct);
        viewModel.CountdownTitle = "Trip";
        viewModel.CountdownTargetUtc = fixture.Clock.UtcNow.AddDays(2);
        await viewModel.SaveCountdownAsync(ct);

        Assert.Equal("Trip in 2 days", viewModel.NextCountdownText);
    }

    [Fact]
    public async Task Next_countdown_text_reports_no_countdowns_when_none_are_saved()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("no countdowns yet ah", viewModel.NextCountdownText);
    }

    [Fact]
    public async Task Next_countdown_text_uses_local_calendar_days_not_a_floored_time_span()
    {
        // 14:00 today to midnight six calendar days later is 5 days 10 hours of raw
        // remaining time -- floor(TotalDays) would read "in 5 days" -- but it is six
        // local calendar dates away and must read "in 6 days".
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-19T14:00:00Z");
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "trip",
                "Trip",
                DateTimeOffset.Parse("2026-09-25T00:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Equal("Trip in 6 days", viewModel.NextCountdownText);
    }

    [Fact]
    public async Task Next_countdown_text_excludes_a_past_countdown_but_keeps_one_due_later_today()
    {
        // An already-past countdown must not keep sorting first and reading "is
        // today" forever; a countdown whose calendar date genuinely is today must
        // still read "is today" even though its exact time has not arrived yet.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "old",
                "Old visit",
                DateTimeOffset.Parse("2026-09-10T12:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "later",
                "Later visit",
                DateTimeOffset.Parse("2026-09-19T20:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Equal("Later visit is today", viewModel.NextCountdownText);
    }

    [Fact]
    public async Task Next_countdown_text_reads_nothing_coming_up_when_every_saved_countdown_is_past()
    {
        // Regression: a non-empty Countdowns list where every entry has already
        // gone by used to read "no countdowns yet ah" -- indistinguishable from
        // an actually empty list.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "old",
                "Old visit",
                DateTimeOffset.Parse("2026-09-10T12:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.NotEmpty(viewModel.Countdowns);
        Assert.Equal("nothing coming up", viewModel.NextCountdownText);
    }

    [Fact]
    public async Task Next_countdown_text_tie_breaks_same_day_countdowns_by_target_time()
    {
        // Two countdowns landing on the same calendar day both sort as "days: 0",
        // so the choice between them must be deterministic (earliest TargetUtc)
        // rather than depending on Countdowns' incidental storage order.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "later-today",
                "Later today",
                DateTimeOffset.Parse("2026-09-19T20:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        await fixture.Countdowns.SaveAsync(
            new Countdown(
                "earlier-today",
                "Earlier today",
                DateTimeOffset.Parse("2026-09-19T12:00:00Z"),
                isAllDay: false,
                TimeZoneInfo.Utc),
            ct);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        Assert.Equal("Earlier today is today", viewModel.NextCountdownText);
    }

    [Fact]
    public void DescribeError_matches_FeatureViewModelBases_exception_to_copy_mapping()
    {
        // Home's code-behind click handlers run outside RunAsync and used to show
        // exception.Message directly; DescribeError must map exceptions the same
        // way RunAsync's ErrorMessage does so Home stays consistent with the
        // other pages.
        Assert.Equal("select one first", HomeViewModel.DescribeError(new ArgumentNullException("thing")));
        Assert.Equal("aiyo bad input", HomeViewModel.DescribeError(new ArgumentException("aiyo bad input")));
        Assert.Equal("cannot finish that try again", HomeViewModel.DescribeError(new InvalidCastException("boom")));
    }

    [Fact]
    public async Task Completing_a_reminder_persists_before_dismissing_pet_state()
    {
        var fixture = FeatureFixture.Create();
        var reminder = fixture.Reminder;
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.CompleteCommand.ExecuteAsync(reminder);

        Assert.Equal(["repository.complete", "pet.dismiss"], fixture.Events);
    }

    [Fact]
    public async Task Completing_a_not_yet_due_reminder_consumes_its_pending_occurrence()
    {
        // Audit regression: completing used to call NextOccurrence with the real
        // "now", which for a not-yet-due reminder just returns NextDueUtc
        // unchanged, so "done" was a no-op and the toast still fired later.
        var fixture = FeatureFixture.Create();
        var reminder = fixture.Reminder with { NextDueUtc = fixture.Clock.UtcNow.AddHours(2) };
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.CompleteCommand.ExecuteAsync(reminder);

        var updated = Assert.Single(viewModel.Reminders);
        Assert.Equal(reminder.Id, updated.Id);
        Assert.Null(updated.NextDueUtc);
    }

    [Fact]
    public async Task Snoozing_a_reminder_with_a_live_snooze_discards_its_held_presentation()
    {
        // Audit regression: CompleteAsync discards a reminder's held/queued
        // presentation via DiscardHeldReminderAsync, but SnoozeAsync did not
        // -- so a snoozed reminder that was currently held would still pop
        // on the next unsuppressed tick. This is the safe case: NextDueUtc
        // (10:00, the fixture clock's "now") is no later than the 15-minute
        // snooze (10:15), so the snooze is "live" and will govern re-delivery
        // on its own -- the held copy is redundant and can be discarded.
        var discarded = new List<string>();
        var fixture = FeatureFixture.Create(discardHeldReminderAsync: (id, _) =>
        {
            discarded.Add(id);
            return Task.CompletedTask;
        });
        var reminder = fixture.Reminder;
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.SnoozeCommand.ExecuteAsync(reminder);

        Assert.Equal([reminder.Id], discarded);
    }

    [Fact]
    public async Task Snoozing_a_reminder_with_a_dead_snooze_does_not_discard_its_held_presentation()
    {
        // Audit regression: the scheduling engine advances NextDueUtc BEFORE
        // notifying, so once an occurrence is held (e.g. a bedtime reminder
        // during quiet hours), the held row is the ONLY record of it --
        // NextDueUtc already points at the occurrence after this one. A
        // 15-minute snooze that resolves before that later NextDueUtc is
        // "dead" (SnoozedUntilUtc < NextDueUtc is ignored by
        // LoadDueAsync/Reconcile), so discarding the held copy here would
        // make the reminder vanish entirely instead of resurfacing once the
        // hold clears.
        var discarded = new List<string>();
        var fixture = FeatureFixture.Create(discardHeldReminderAsync: (id, _) =>
        {
            discarded.Add(id);
            return Task.CompletedTask;
        });
        var reminder = fixture.Reminder with { NextDueUtc = DateTimeOffset.Parse("2026-09-13T10:00:00Z") };
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.SnoozeCommand.ExecuteAsync(reminder);

        Assert.Empty(discarded);
    }

    [Fact]
    public async Task Completing_a_reminder_still_reports_success_when_the_notification_dismiss_fails()
    {
        // Opus review follow-up 6: after the completion is durably saved,
        // a throw from a best-effort cleanup call (notification dismiss)
        // must not surface as a failure for a completion that already
        // succeeded, and must not skip the other best-effort cleanup (the
        // held-copy discard) that follows it.
        var discarded = new List<string>();
        var fixture = FeatureFixture.Create(
            dismissReminderNotificationAsync: (_, _) => throw new InvalidOperationException("dismiss boom"),
            discardHeldReminderAsync: (id, _) =>
            {
                discarded.Add(id);
                return Task.CompletedTask;
            });
        var reminder = fixture.Reminder;
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.CompleteCommand.ExecuteAsync(reminder);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("yayyy done le good job", viewModel.StatusMessage);
        Assert.Equal([reminder.Id], discarded);
    }

    [Fact]
    public async Task Snoozing_a_reminder_still_reports_success_when_the_notification_dismiss_fails()
    {
        // Opus review follow-up 6: same as completion -- a dismiss failure
        // after the snooze is durably saved must not report a failure,
        // and must not skip the held-copy discard check that follows it.
        var discarded = new List<string>();
        var fixture = FeatureFixture.Create(
            dismissReminderNotificationAsync: (_, _) => throw new InvalidOperationException("dismiss boom"),
            discardHeldReminderAsync: (id, _) =>
            {
                discarded.Add(id);
                return Task.CompletedTask;
            });
        var reminder = fixture.Reminder; // NextDueUtc (10:00) <= snoozeUntil (10:15): a live snooze.
        fixture.Reminders.Items.Add(reminder);
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.SnoozeCommand.ExecuteAsync(reminder);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("otayyy snoozed for 15 min", viewModel.StatusMessage);
        Assert.Equal([reminder.Id], discarded);
    }

    [Fact]
    public async Task Saving_a_note_clears_the_editor_so_fresh_text_creates_a_new_note()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new LoveNotesViewModel(fixture.Context);

        viewModel.DraftText = "first";
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);

        Assert.Null(viewModel.SelectedNote);
        Assert.Equal(string.Empty, viewModel.DraftText);
        Assert.Equal("first", Assert.Single(fixture.LocalNotes.Notes).Text);

        viewModel.DraftText = "second";
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);

        Assert.Equal(2, fixture.LocalNotes.Notes.Count);
        Assert.Equal("first", fixture.LocalNotes.Notes[0].Text);
        Assert.Equal("second", fixture.LocalNotes.Notes[1].Text);
    }

    [Fact]
    public async Task Saving_a_reminder_clears_the_editor_so_fresh_text_creates_a_new_reminder()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new RemindersViewModel(fixture.Context);

        viewModel.Title = "first";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(viewModel.SelectedReminder);
        Assert.Equal(string.Empty, viewModel.Title);
        Assert.Single(fixture.Reminders.Items);

        viewModel.Title = "second";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, fixture.Reminders.Items.Count);
        Assert.Equal("first", fixture.Reminders.Items[0].Title);
        Assert.Equal("second", fixture.Reminders.Items[1].Title);
    }

    [Fact]
    public async Task Null_selection_reports_select_one_first_instead_of_internals()
    {
        var fixture = FeatureFixture.Create();
        var reminders = new RemindersViewModel(fixture.Context);
        await reminders.CompleteCommand.ExecuteAsync(null);
        Assert.Equal("select one first", reminders.ErrorMessage);
        await reminders.SnoozeCommand.ExecuteAsync(null);
        Assert.Equal("select one first", reminders.ErrorMessage);

        var notes = new LoveNotesViewModel(fixture.Context);
        await notes.DeleteLocalNoteCommand.ExecuteAsync(null);
        Assert.Equal("select one first", notes.ErrorMessage);
        await notes.RevealRemoteNoteCommand.ExecuteAsync(null);
        Assert.Equal("select one first", notes.ErrorMessage);

        var home = new HomeViewModel(fixture.Context);
        await home.DeleteCountdownCommand.ExecuteAsync(null);
        Assert.Equal("select one first", home.ErrorMessage);

        var tasks = new TasksFocusViewModel(fixture.Context);
        await tasks.CompleteTaskCommand.ExecuteAsync(null);
        Assert.Equal("select one first", tasks.ErrorMessage);
        await tasks.DeleteTaskCommand.ExecuteAsync(null);
        Assert.Equal("select one first", tasks.ErrorMessage);
    }

    [Fact]
    public async Task ArgumentException_error_text_strips_the_framework_parameter_suffix()
    {
        // Regression: ArgumentException.Message appends " (Parameter 'GlobalShortcut')" from
        // ParamName. That framework wording leaked straight into the user-visible error text.
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(fixture.Context) { GlobalShortcut = "   " };

        await viewModel.SaveShortcutAsync(TestContext.Current.CancellationToken);

        Assert.Equal("wait type a shortcut first", viewModel.ErrorMessage);
        Assert.DoesNotContain("Parameter", viewModel.ErrorMessage);
    }

    [Fact]
    public void ArgumentOutOfRangeException_error_text_strips_the_suffix_and_the_trailing_actual_value_line()
    {
        // Regression: the old strip only matched an EndsWith on " (Parameter 'name')".
        // ArgumentOutOfRangeException appends "Actual value was X." AFTER that suffix, so the
        // whole framework tail (suffix and actual-value line) leaked straight through untouched.
        var exception = new ArgumentOutOfRangeException("count", 5, "must be between 1 and 3");

        var message = InvokeToUserMessage(exception);

        Assert.Equal("must be between 1 and 3", message);
        Assert.DoesNotContain("Parameter", message);
        Assert.DoesNotContain("Actual value", message);
    }

    [Fact]
    public void ArgumentException_with_a_blank_message_falls_back_to_the_generic_copy_instead_of_going_blank()
    {
        // Regression: stripping "" + " (Parameter 'p')" produced an empty string, which would
        // hide the whole error panel instead of telling the user anything went wrong at all.
        var exception = new ArgumentException(string.Empty, "shortcut");

        var message = InvokeToUserMessage(exception);

        Assert.Equal("cannot finish that try again", message);
    }

    /// <summary>ToUserMessage is `protected static` on FeatureViewModelBase with no reachable
    /// call site that throws ArgumentOutOfRangeException or a blank-message ArgumentException,
    /// so these two edge cases are exercised directly via reflection (the same non-public-member
    /// access pattern already used in WinUiHardeningTests for TrayIconService's private fields).</summary>
    private static string InvokeToUserMessage(Exception exception)
    {
        var method = typeof(FeatureViewModelBase).GetMethod(
            "ToUserMessage", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Exception)])
            ?? throw new InvalidOperationException("FeatureViewModelBase.ToUserMessage was not found.");
        return (string)method.Invoke(null, [exception])!;
    }

    [Fact]
    public void Request_delete_commands_report_select_one_first_on_a_null_selection_and_do_not_open_the_panel()
    {
        // H-2: the three RequestDelete...Commands used to be silent no-ops with a null
        // selection (the button that runs them stays enabled with nothing selected).
        var fixture = FeatureFixture.Create();

        var home = new HomeViewModel(fixture.Context);
        home.RequestDeleteCountdownCommand.Execute(null);
        Assert.Equal("select one first", home.ErrorMessage);
        Assert.False(home.IsConfirmingDeleteCountdown);

        var tasks = new TasksFocusViewModel(fixture.Context);
        tasks.RequestDeleteTaskCommand.Execute(null);
        Assert.Equal("select one first", tasks.ErrorMessage);
        Assert.False(tasks.IsConfirmingDeleteTask);

        var notes = new LoveNotesViewModel(fixture.Context);
        notes.RequestDeleteLocalNoteCommand.Execute(null);
        Assert.Equal("select one first", notes.ErrorMessage);
        Assert.False(notes.IsConfirmingDeleteNote);
    }

    [Fact]
    public async Task Request_delete_commands_clear_a_previous_error_message_on_a_valid_selection()
    {
        var fixture = FeatureFixture.Create();

        var home = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Anniversary",
            CountdownTargetUtc = DateTimeOffset.Parse("2026-12-01T12:00:00Z"),
        };
        await home.SaveCountdownAsync(TestContext.Current.CancellationToken);
        var countdown = Assert.Single(fixture.Countdowns.Items);
        home.RequestDeleteCountdownCommand.Execute(null);
        Assert.NotNull(home.ErrorMessage);

        home.RequestDeleteCountdownCommand.Execute(countdown);
        Assert.Null(home.ErrorMessage);
        Assert.True(home.IsConfirmingDeleteCountdown);
    }

    [Fact]
    public async Task Countdown_deletion_requires_a_separate_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Anniversary",
            CountdownTargetUtc = DateTimeOffset.Parse("2026-12-01T12:00:00Z"),
        };
        await viewModel.SaveCountdownAsync(TestContext.Current.CancellationToken);
        var countdown = Assert.Single(fixture.Countdowns.Items);

        viewModel.RequestDeleteCountdownCommand.Execute(countdown);
        Assert.True(viewModel.IsConfirmingDeleteCountdown);
        Assert.Equal(countdown, viewModel.PendingDeleteCountdown);
        Assert.Single(fixture.Countdowns.Items);

        viewModel.CancelDeleteCountdownCommand.Execute(null);
        Assert.False(viewModel.IsConfirmingDeleteCountdown);
        Assert.Single(fixture.Countdowns.Items);

        viewModel.RequestDeleteCountdownCommand.Execute(countdown);
        await viewModel.DeleteCountdownCommand.ExecuteAsync(viewModel.PendingDeleteCountdown);

        Assert.Empty(fixture.Countdowns.Items);
        Assert.False(viewModel.IsConfirmingDeleteCountdown);
    }

    [Fact]
    public async Task Task_deletion_requires_a_separate_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "Book dinner" };
        await viewModel.SaveTaskAsync(TestContext.Current.CancellationToken);
        var task = Assert.Single(fixture.Tasks.Items);

        viewModel.RequestDeleteTaskCommand.Execute(task);
        Assert.True(viewModel.IsConfirmingDeleteTask);
        Assert.Equal(task, viewModel.PendingDeleteTask);
        Assert.Single(fixture.Tasks.Items);

        viewModel.CancelDeleteTaskCommand.Execute(null);
        Assert.False(viewModel.IsConfirmingDeleteTask);
        Assert.Single(fixture.Tasks.Items);

        viewModel.RequestDeleteTaskCommand.Execute(task);
        await viewModel.DeleteTaskCommand.ExecuteAsync(viewModel.PendingDeleteTask);

        Assert.Empty(fixture.Tasks.Items);
        Assert.False(viewModel.IsConfirmingDeleteTask);
    }

    [Fact]
    public async Task Local_note_deletion_requires_a_separate_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context) { DraftText = "first" };
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);
        var note = Assert.Single(fixture.LocalNotes.Notes);

        viewModel.RequestDeleteLocalNoteCommand.Execute(note);
        Assert.True(viewModel.IsConfirmingDeleteNote);
        Assert.Equal(note, viewModel.PendingDeleteNote);
        Assert.Single(fixture.LocalNotes.Notes);

        viewModel.CancelDeleteLocalNoteCommand.Execute(null);
        Assert.False(viewModel.IsConfirmingDeleteNote);
        Assert.Single(fixture.LocalNotes.Notes);

        viewModel.RequestDeleteLocalNoteCommand.Execute(note);
        await viewModel.DeleteLocalNoteCommand.ExecuteAsync(viewModel.PendingDeleteNote);

        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.False(viewModel.IsConfirmingDeleteNote);
    }

    [Fact]
    public async Task Deleting_a_local_note_matches_by_id_even_if_the_caller_passes_a_stale_copy()
    {
        // L-2: LocalLoveNote is a record, so removing it from the collection by value
        // equality silently no-ops if the object passed to Delete differs from the one
        // stored (e.g. a stale UI-bound copy with a different Enabled/Text value).
        var fixture = FeatureFixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context) { DraftText = "first" };
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);
        var stored = Assert.Single(fixture.LocalNotes.Notes);
        var staleCopy = stored with { Enabled = !stored.Enabled };

        await viewModel.DeleteLocalNoteCommand.ExecuteAsync(staleCopy);

        Assert.Empty(fixture.LocalNotes.Notes);
    }

    [Fact]
    public async Task Selecting_a_different_countdown_clears_the_pending_delete_and_the_prompt_names_the_target()
    {
        // H-1: PendingDeleteCountdown used to float free of SelectedCountdown, so
        // select A, request delete A, select B, confirm -> deleted A instead of B.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Anniversary",
            CountdownTargetUtc = DateTimeOffset.Parse("2026-12-01T12:00:00Z"),
        };
        await viewModel.SaveCountdownAsync(ct);
        viewModel.CountdownTitle = "Birthday";
        viewModel.CountdownTargetUtc = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        await viewModel.SaveCountdownAsync(ct);
        Assert.Equal(2, fixture.Countdowns.Items.Count);
        var first = fixture.Countdowns.Items.First(item => item.Title == "Anniversary");
        var second = fixture.Countdowns.Items.First(item => item.Title == "Birthday");

        viewModel.RequestDeleteCountdownCommand.Execute(first);
        Assert.Equal($"delete \"{first.Title}\" for good? cannot undo", viewModel.DeleteCountdownPrompt);

        viewModel.SelectCountdown(second);

        Assert.False(viewModel.IsConfirmingDeleteCountdown);
        Assert.Null(viewModel.PendingDeleteCountdown);
        Assert.Null(viewModel.DeleteCountdownPrompt);
        Assert.Equal(2, fixture.Countdowns.Items.Count);
    }

    [Fact]
    public async Task Selecting_a_different_task_clears_the_pending_delete_and_the_prompt_names_the_target()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "Book dinner" };
        await viewModel.SaveTaskAsync(ct);
        viewModel.Title = "Buy flowers";
        await viewModel.SaveTaskAsync(ct);
        Assert.Equal(2, fixture.Tasks.Items.Count);
        var first = fixture.Tasks.Items.First(item => item.Title == "Book dinner");
        var second = fixture.Tasks.Items.First(item => item.Title == "Buy flowers");

        viewModel.RequestDeleteTaskCommand.Execute(first);
        Assert.Equal($"delete \"{first.Title}\" for good? cannot undo", viewModel.DeleteTaskPrompt);

        viewModel.SelectTask(second);

        Assert.False(viewModel.IsConfirmingDeleteTask);
        Assert.Null(viewModel.PendingDeleteTask);
        Assert.Null(viewModel.DeleteTaskPrompt);
        Assert.Equal(2, fixture.Tasks.Items.Count);
    }

    [Fact]
    public async Task Selecting_a_different_note_clears_the_pending_delete_and_the_prompt_names_the_target()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context) { DraftText = "first note" };
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);
        viewModel.DraftText = "second note";
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.LocalNotes.Notes.Count);
        var first = fixture.LocalNotes.Notes[0];
        var second = fixture.LocalNotes.Notes[1];

        viewModel.RequestDeleteLocalNoteCommand.Execute(first);
        Assert.Equal($"delete \"{first.Text}\" for good? cannot undo", viewModel.DeleteNotePrompt);

        viewModel.SelectedNote = second;

        Assert.False(viewModel.IsConfirmingDeleteNote);
        Assert.Null(viewModel.PendingDeleteNote);
        Assert.Null(viewModel.DeleteNotePrompt);
        Assert.Equal(2, fixture.LocalNotes.Notes.Count);
    }

    [Fact]
    public void Delete_prompt_truncation_does_not_split_an_emoji_straddling_the_cut()
    {
        // Regression: text[..40] cuts by UTF-16 code unit, so a note whose
        // emoji spans indices 39/40 used to split the surrogate pair and
        // show a garbage glyph in the delete prompt. The cut must back up
        // one char instead of landing inside the pair.
        var fixture = FeatureFixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context);
        var text = new string('a', 39) + "😀" + " more text after the emoji";
        var note = new LocalLoveNote("note-1", text);

        viewModel.RequestDeleteLocalNoteCommand.Execute(note);

        Assert.Equal(
            $"delete \"{new string('a', 39)}…\" for good? cannot undo",
            viewModel.DeleteNotePrompt);
    }

    [Fact]
    public async Task Completing_a_task_clears_a_pending_delete_confirmation_for_the_same_task()
    {
        // M-4: CompleteTaskAsync changes the task's state outside the delete flow, so a
        // pending "delete this task" confirmation for it would otherwise go stale.
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "Book dinner" };
        await viewModel.SaveTaskAsync(TestContext.Current.CancellationToken);
        var task = Assert.Single(fixture.Tasks.Items);

        viewModel.RequestDeleteTaskCommand.Execute(task);
        Assert.True(viewModel.IsConfirmingDeleteTask);

        await viewModel.CompleteTaskCommand.ExecuteAsync(task);

        Assert.False(viewModel.IsConfirmingDeleteTask);
        Assert.Null(viewModel.PendingDeleteTask);
    }

    [Fact]
    public async Task Refreshing_the_home_view_model_clears_a_stale_pending_delete_confirmation()
    {
        // M-4: pages/view models are cached by the shell, so a revisit must not show a
        // stale red confirm panel left over from a previous visit.
        var fixture = FeatureFixture.Create();
        var viewModel = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Trip",
            CountdownTargetUtc = DateTimeOffset.Parse("2026-12-01T12:00:00Z"),
        };
        await viewModel.SaveCountdownAsync(TestContext.Current.CancellationToken);
        var countdown = Assert.Single(fixture.Countdowns.Items);
        viewModel.RequestDeleteCountdownCommand.Execute(countdown);
        Assert.True(viewModel.IsConfirmingDeleteCountdown);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsConfirmingDeleteCountdown);
        Assert.Null(viewModel.PendingDeleteCountdown);
    }

    [Fact]
    public async Task Refreshing_the_tasks_view_model_clears_a_stale_pending_delete_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context) { Title = "Book dinner" };
        await viewModel.SaveTaskAsync(TestContext.Current.CancellationToken);
        var task = Assert.Single(fixture.Tasks.Items);
        viewModel.RequestDeleteTaskCommand.Execute(task);
        Assert.True(viewModel.IsConfirmingDeleteTask);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsConfirmingDeleteTask);
        Assert.Null(viewModel.PendingDeleteTask);
    }

    [Fact]
    public async Task Refreshing_the_love_notes_view_model_clears_a_stale_pending_delete_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new LoveNotesViewModel(fixture.Context) { DraftText = "first" };
        await viewModel.SaveLocalNoteCommand.ExecuteAsync(null);
        var note = Assert.Single(fixture.LocalNotes.Notes);
        viewModel.RequestDeleteLocalNoteCommand.Execute(note);
        Assert.True(viewModel.IsConfirmingDeleteNote);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsConfirmingDeleteNote);
        Assert.Null(viewModel.PendingDeleteNote);
    }

    [Fact]
    public async Task Routine_reminder_with_unknown_time_zone_still_presents_in_utc()
    {
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var published = new List<DurableNotification>();
        var sink = new ReminderDueSink(fixture.Reminders, () => new CapturingGateway(published));
        var reminder = fixture.Reminder with
        {
            Id = LocalReminderDefaults.EveningCheckInId,
            LocalTimeZoneId = "Nope/Nowhere",
        };
        fixture.Reminders.Items.Add(reminder);

        await sink.NotifyAsync(
            new ReminderOccurrence(reminder.Id, DateTimeOffset.Parse("2026-09-12T10:00:00Z")),
            ct);

        var item = Assert.Single(published);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero), item.ExpiresUtc);
    }

    private sealed class CapturingGateway(List<DurableNotification> published) : IUnsolicitedPresentationGateway
    {
        public Task PublishAsync(DurableNotification item, bool bypassSuppression, CancellationToken cancellationToken)
        {
            published.Add(item);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Saving_a_remote_note_to_jar_is_explicit()
    {
        var fixture = FeatureFixture.Create();
        var envelope = new RemoteEnvelope("message-1", [1], fixture.Clock.UtcNow);
        fixture.RemoteNotes.Pending.Add(envelope);
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.LocalNotes.Notes);
        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(envelope);
        Assert.Empty(fixture.LocalNotes.Notes);

        await viewModel.SaveOpenedNoteCommand.ExecuteAsync(null);

        Assert.Equal("You can do it", Assert.Single(fixture.LocalNotes.Notes).Text);
        Assert.Empty(fixture.RemoteNotes.Pending);
        Assert.Contains("pet.dismiss", fixture.Events);
    }

    [Fact]
    public async Task Privacy_destructive_actions_require_a_separate_confirmation()
    {
        var calls = new List<string>();
        var fixture = FeatureFixture.Create(
            restoreAsync: _ => { calls.Add("restore"); return Task.CompletedTask; },
            deleteLocalDataAsync: _ => { calls.Add("local"); return Task.CompletedTask; },
            deleteRemoteDataAsync: _ => { calls.Add("remote"); return Task.CompletedTask; });
        var viewModel = new PrivacyDataViewModel(fixture.Context);

        viewModel.RequestDeleteLocalDataCommand.Execute(null);
        Assert.Empty(calls);
        Assert.Equal(PrivacyConfirmationAction.DeleteLocal, viewModel.PendingConfirmation);
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));

        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["local"], calls);
        Assert.Equal(PrivacyConfirmationAction.None, viewModel.PendingConfirmation);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_privacy_action_keeps_confirmation_available_for_retry()
    {
        var fixture = FeatureFixture.Create(
            restoreAsync: _ => Task.FromException(new IOException("restore failed")));
        var viewModel = new PrivacyDataViewModel(fixture.Context);

        viewModel.RequestRestoreCommand.Execute(null);
        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrivacyConfirmationAction.Restore, viewModel.PendingConfirmation);
        Assert.Equal("cannot finish that try again", viewModel.ErrorMessage);
        Assert.DoesNotContain("restore failed", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Appearance_save_applies_the_shell_theme_only_after_persistence_succeeds()
    {
        var fixture = FeatureFixture.Create();
        var applied = new List<AppTheme>();
        var viewModel = new AppearanceViewModel(fixture.Context, applied.Add) { Theme = AppTheme.Dark };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal([AppTheme.Dark], applied);
        fixture.Preferences.FailNextSave = true;
        viewModel.Theme = AppTheme.Light;
        await viewModel.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal([AppTheme.Dark], applied);
    }

    [Fact]
    public async Task Appearance_save_commits_a_changed_pet_scale_to_the_existing_placement_row()
    {
        // Regression: "save appearance" used to only persist Theme/ReducedMotion/etc and
        // silently drop whatever the user had just set on the pet-size slider -- only the
        // separate "save pet placement" button (SavePlacementCommand) committed PetScale.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("current monitor", 0.3, 0.4, 1.0), ct);
        fixture.Placements.SaveHistory.Clear(); // ignore the setup write above
        var viewModel = new AppearanceViewModel(fixture.Context) { PetScale = 1.5 };

        await viewModel.SaveAsync(ct);

        var saved = Assert.Single(fixture.Placements.SaveHistory);
        Assert.Equal("current monitor", saved.MonitorDeviceName);
        Assert.Equal(1.5, saved.Scale);
        Assert.Equal(0.3, saved.NormalizedX);
        Assert.Equal(0.4, saved.NormalizedY);
    }

    [Fact]
    public async Task Appearance_save_does_not_rewrite_the_placement_when_the_scale_slider_was_not_touched()
    {
        // Regression: "save appearance" re-applied PetScale on every save regardless of whether
        // the user had touched the slider, silently re-moving/resizing the live pet on an
        // unrelated theme/preferences save.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("current monitor", 0.3, 0.4, 1.0), ct);
        fixture.Placements.SaveHistory.Clear();
        var viewModel = new AppearanceViewModel(fixture.Context); // PetScale left at its ctor default

        await viewModel.SaveAsync(ct);

        Assert.Empty(fixture.Placements.SaveHistory);
    }

    [Fact]
    public async Task Appearance_save_does_not_create_a_placement_row_for_an_unregistered_monitor()
    {
        // Regression: when no placement row matched MonitorDeviceName, "save appearance" used to
        // create a phantom row at the default (0.8, 0.8) and teleport the pet there. Only the
        // explicit "save pet placement" button may create a new row.
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(fixture.Context) { PetScale = 1.5 };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Placements.SaveHistory);
    }

    [Fact]
    public async Task Save_placement_button_still_creates_a_new_row_for_an_unregistered_monitor()
    {
        // The explicit "save pet placement" button (as opposed to "save appearance") is the one
        // allowed to create a placement row from scratch -- that behaviour must not change.
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(fixture.Context) { PetScale = 1.5 };

        await viewModel.SavePlacementAsync(TestContext.Current.CancellationToken);

        var saved = Assert.Single(fixture.Placements.SaveHistory);
        Assert.Equal("current monitor", saved.MonitorDeviceName);
        Assert.Equal(1.5, saved.Scale);
    }

    [Fact]
    public async Task Changing_monitor_reloads_pet_scale_from_that_monitor_saved_placement()
    {
        // Regression: switching MonitorDeviceName (e.g. on a multi-monitor
        // machine) left PetScale showing whichever monitor's scale was
        // loaded at RefreshAsync time, instead of the newly selected
        // monitor's own saved scale.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("current monitor", 0.3, 0.4, 1.0), ct);
        await fixture.Placements.SaveAsync(new PetPlacement("second monitor", 0.5, 0.5, 1.8), ct);
        var viewModel = new AppearanceViewModel(fixture.Context);
        await viewModel.RefreshAsync(ct);
        Assert.Equal("current monitor", viewModel.MonitorDeviceName);
        Assert.Equal(1.0, viewModel.PetScale);

        viewModel.MonitorDeviceName = "second monitor";

        Assert.Equal(1.8, viewModel.PetScale);

        viewModel.MonitorDeviceName = "current monitor";

        Assert.Equal(1.0, viewModel.PetScale);
    }

    [Fact]
    public async Task Changing_monitor_to_one_with_no_saved_placement_leaves_pet_scale_untouched()
    {
        // A monitor with no placement row yet (never used before) must not
        // reset/zero out whatever scale was showing -- the slider keeps its
        // current value until the user picks one or saves a new placement.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        await fixture.Placements.SaveAsync(new PetPlacement("current monitor", 0.3, 0.4, 1.0), ct);
        var viewModel = new AppearanceViewModel(fixture.Context);
        await viewModel.RefreshAsync(ct);
        Assert.Equal(1.0, viewModel.PetScale);

        viewModel.MonitorDeviceName = "brand new monitor";

        Assert.Equal(1.0, viewModel.PetScale);
    }

    [Fact]
    public async Task Appearance_saves_sound_preferences_through_the_mutation_coordinator()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(fixture.Context)
        {
            SoundsEnabled = false,
            SoundVolume = 0.72,
        };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        var saved = Assert.Single(fixture.Preferences.SaveHistory);
        Assert.False(saved.SoundsEnabled);
        Assert.Equal(0.72, saved.SoundVolume);
    }

    [Fact]
    public async Task Appearance_saves_manual_outfit_and_recurring_seasonal_dates()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(
            fixture.Context,
            availableOutfitKeys: ["base", "winter"])
        {
            SelectedOutfit = "winter",
            AnniversaryDate = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
            BirthdayDate = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
        };

        await viewModel.ApplyOutfitAsync(TestContext.Current.CancellationToken);

        var saved = Assert.Single(fixture.Preferences.SaveHistory);
        Assert.Equal("winter", saved.OutfitKey);
        Assert.False(saved.AutomaticSeasonalMode);
        Assert.Equal(new MonthDay(9, 12), saved.Anniversary);
        Assert.Equal(new MonthDay(2, 28), saved.Birthday);
    }

    [Fact]
    public async Task Appearance_refresh_preserves_february_29_dates_in_non_leap_years()
    {
        var fixture = FeatureFixture.Create();
        await fixture.Context.UpdatePreferencesAsync(
            current => current with { Birthday = new MonthDay(2, 29) },
            TestContext.Current.CancellationToken);
        var viewModel = new AppearanceViewModel(fixture.Context);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.BirthdayDate?.Month);
        Assert.Equal(29, viewModel.BirthdayDate?.Day);
    }

    [Fact]
    public void Appearance_reports_the_automatic_outfit_for_the_current_local_date()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new AppearanceViewModel(
            fixture.Context,
            availableOutfitKeys: ["base", "anniversary"])
        {
            AnniversaryDate = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("automatic mode · anniversary today", viewModel.OutfitAvailabilityMessage);
    }

    [Fact]
    public async Task Native_overlay_dispatch_queue_owns_faults_and_preserves_click_order()
    {
        var fixture = FeatureFixture.Create();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var firstRouter = new OverlayCommandRouter(fixture.Context, async (_, _) =>
        {
            calls.Add("first");
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        });
        var secondRouter = new OverlayCommandRouter(fixture.Context, (_, _) =>
        {
            calls.Add("second");
            return Task.CompletedTask;
        });
        using var first = new OverlayActionSurfaceController();
        using var second = new OverlayActionSurfaceController();
        first.Bind(firstRouter);
        second.Bind(secondRouter);
        first.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        second.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var firstPoint = Center(first.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.Tasks).HitRegion);
        var secondPoint = Center(second.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.Tasks).HitRegion);
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.Enqueue(first, firstPoint);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        queue.Enqueue(second, secondPoint);
        Assert.Equal(["first"], calls);
        releaseFirst.TrySetResult();
        await queue.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], calls);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Presented_action_dispatch_does_not_reread_reflowed_hit_geometry()
    {
        var fixture = FeatureFixture.Create();
        var destinations = new List<string>();
        var router = new OverlayCommandRouter(fixture.Context, (destination, _) =>
        {
            destinations.Add(destination);
            return Task.CompletedTask;
        });
        using var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var presentedTasks = surface.CreateRenderSnapshot().Actions
            .Single(action => action.PrimaryAction == OverlayAction.Tasks);

        surface.UpdateViewport(new PixelRect(100, 100, 900, 700));
        await surface.HandlePresentedActionAsync(
            presentedTasks,
            TestContext.Current.CancellationToken);

        Assert.Equal(["tasks"], destinations);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
    }

    [Fact]
    public async Task Queued_close_cancels_an_active_breathing_action_before_dispatching()
    {
        var fixture = FeatureFixture.Create();
        var breathingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            async (_, cancellationToken) =>
            {
                breathingEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var comfort = surface.CreateRenderSnapshot().Actions
            .Single(action => action.PrimaryAction == OverlayAction.ComfortMe);
        await surface.HandlePresentedActionAsync(comfort, TestContext.Current.CancellationToken);
        var snapshot = surface.CreateRenderSnapshot();
        var breathe = snapshot.Actions.Single(action => action.ComfortAction == ComfortAction.BreatheWithMe);
        var close = snapshot.Actions.Single(action => action.ComfortAction == ComfortAction.Close);
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.Enqueue(surface, breathe);
        await breathingEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        queue.Enqueue(surface, close);
        await queue.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
        Assert.False(router.IsBreathing);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Disposed_overlay_dispatch_queue_finishes_cancelled_work()
    {
        var fixture = FeatureFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(fixture.Context, async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var tasks = surface.CreateRenderSnapshot().Actions
            .Single(action => action.PrimaryAction == OverlayAction.Tasks);
        var reported = new List<Exception>();
        var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.Enqueue(surface, tasks);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        queue.Dispose();
        await queue.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task Stale_pointer_completion_cannot_close_a_newly_reopened_surface()
    {
        var fixture = FeatureFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(fixture.Context, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        using var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        var viewport = new PixelRect(0, 0, 640, 480);
        var anchor = new PixelPoint(320, 400);
        surface.Open(viewport, anchor);
        var point = Center(surface.Arrangement!.PrimaryActions
            .Single(item => item.Action == OverlayAction.Tasks).HitRegion);

        var dispatch = surface.HandlePointerAsync(point, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        surface.Close();
        surface.Open(viewport, anchor);
        release.TrySetResult();
        await dispatch;

        Assert.Equal(OverlayActionSurfaceKind.Primary, surface.Kind);
        Assert.NotNull(surface.Arrangement);
    }

    [Fact]
    public void Comfort_surface_has_exactly_the_approved_actions()
    {
        Assert.Equal(
            [
                ComfortAction.BreatheWithMe,
                ComfortAction.TinyHug,
                ComfortAction.ReadALoveNote,
                ComfortAction.TakeAFiveMinuteBreak,
                ComfortAction.Close,
            ],
            OverlayCommandRouter.ComfortActions);
    }

    [Fact]
    public async Task Router_fails_explicitly_when_a_settings_destination_is_not_wired()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(fixture.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.ExecuteAsync(OverlayAction.Tasks, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Router_pet_greeting_restores_the_next_durable_pet_state()
    {
        var fixture = FeatureFixture.Create();
        fixture.Context.Pet.Handle(new PetEvent.ReminderDue("next-reminder"));
        var router = new OverlayCommandRouter(fixture.Context);

        await router.ExecuteAsync(OverlayAction.Pet, TestContext.Current.CancellationToken);

        Assert.Equal(PetState.Reminder, fixture.Context.Pet.Current.State);
        Assert.Equal(
            PetState.Idle,
            fixture.Context.Pet.Handle(new PetEvent.Dismissed("next-reminder")).State);
    }

    [Fact]
    public async Task Breathing_publishes_a_finite_cycle_and_five_minute_pause()
    {
        var fixture = FeatureFixture.Create();
        var phases = new List<BreathVisualPhase>();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        router.ComfortPanelChanged += (_, _) => phases.Add(router.ComfortPanel.Phase);

        await router.ExecuteComfortAsync(ComfortAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await router.ExecuteComfortAsync(ComfortAction.TakeAFiveMinuteBreak, TestContext.Current.CancellationToken);

        Assert.False(router.IsBreathing);
        Assert.Equal(BreathVisualPhase.Complete, router.ComfortPanel.Phase);
        Assert.Contains(BreathVisualPhase.Inhale, phases);
        Assert.Contains(BreathVisualPhase.Exhale, phases);
        Assert.Equal(PauseMode.FiveMinutes, fixture.Context.GetPauseState().Mode);
    }

    [Fact]
    public async Task Breathing_cancellation_returns_the_comfort_surface_to_idle()
    {
        var fixture = FeatureFixture.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var cancellation = new CancellationTokenSource();
        var breathing = router.ExecuteComfortAsync(ComfortAction.BreatheWithMe, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => breathing);

        Assert.False(router.IsBreathing);
        Assert.Equal(BreathVisualPhase.Idle, router.ComfortPanel.Phase);
    }

    [Fact]
    public async Task Closing_during_breathing_keeps_the_comfort_panel_closed()
    {
        var fixture = FeatureFixture.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        router.OpenComfortPanel();
        var breathing = router.ExecuteComfortAsync(
            ComfortAction.BreatheWithMe,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await router.ExecuteComfortAsync(ComfortAction.Close, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => breathing);

        Assert.False(router.ComfortPanel.IsOpen);
    }

    [Fact]
    public async Task Offline_pairing_never_reports_a_code_as_ready()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new ConnectionViewModel(fixture.Context);

        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Null(viewModel.PairingCode);
    }

    [Fact]
    public async Task Connection_destructive_actions_require_a_separate_confirmation()
    {
        // F3: mirrors Privacy_destructive_actions_require_a_separate_confirmation. Available is
        // required here because IPairingService.RevokeSessionsWithResultAsync refuses while
        // Offline, and the point of this test is to prove the confirmation gate -- not that
        // refusal -- withholds the call.
        var pairing = new FakePairing { State = PairingAvailability.Available };
        var fixture = FeatureFixture.Create(pairing: pairing);
        var viewModel = new ConnectionViewModel(fixture.Context);

        viewModel.RequestRevokeSessionsCommand.Execute(null);
        Assert.Equal(0, pairing.DisconnectSenderSessionsCallCount);
        Assert.Equal(ConnectionConfirmationAction.RevokeSessions, viewModel.PendingConfirmation);
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));

        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pairing.DisconnectSenderSessionsCallCount);
        Assert.Equal(ConnectionConfirmationAction.None, viewModel.PendingConfirmation);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void Cancelling_a_pending_connection_confirmation_never_calls_the_pairing_service()
    {
        var pairing = new FakePairing { State = PairingAvailability.Available };
        var fixture = FeatureFixture.Create(pairing: pairing);
        var viewModel = new ConnectionViewModel(fixture.Context);

        viewModel.RequestDeleteRemoteDeviceCommand.Execute(null);
        Assert.Equal(ConnectionConfirmationAction.DeleteRemoteDevice, viewModel.PendingConfirmation);

        viewModel.CancelConfirmationCommand.Execute(null);

        Assert.Equal(ConnectionConfirmationAction.None, viewModel.PendingConfirmation);
        Assert.Equal(0, pairing.DeleteRemoteDeviceCallCount);
    }

    [Fact]
    public async Task Forget_pairing_works_locally_even_while_pairing_needs_repair()
    {
        // F2's escape hatch: unlike revoke/delete above, forgetting the pairing never calls
        // GetStateAsync and must succeed with the relay unreachable (or, in production, with a
        // secret store DPAPI cannot read) -- that combination is exactly why it exists. Starting
        // from NeedsRepair (not the default Offline) makes the final Availability assertion below
        // meaningful: ForgetPairingAsync always forces Availability to Offline regardless of
        // where it started, and NeedsRepair is the actual scenario this escape hatch exists for.
        var pairing = new FakePairing { State = PairingAvailability.NeedsRepair };
        var fixture = FeatureFixture.Create(pairing: pairing);
        var viewModel = new ConnectionViewModel(fixture.Context);

        viewModel.RequestForgetPairingCommand.Execute(null);
        Assert.Equal(0, pairing.ForgetPairingCallCount);
        Assert.Equal(ConnectionConfirmationAction.ForgetPairing, viewModel.PendingConfirmation);

        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pairing.ForgetPairingCallCount);
        Assert.Equal(ConnectionConfirmationAction.None, viewModel.PendingConfirmation);
        Assert.Equal(PairingAvailability.Offline, viewModel.Availability);
    }

    [Fact]
    public async Task Preference_updates_merge_against_the_shared_current_snapshot()
    {
        var fixture = FeatureFixture.Create();
        await fixture.Context.UpdatePreferencesAsync(current => current with { Theme = AppTheme.Dark }, TestContext.Current.CancellationToken);
        await fixture.Context.UpdatePreferencesAsync(current => current with { ReducedMotion = true }, TestContext.Current.CancellationToken);

        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.True(fixture.Context.CurrentPreferences.ReducedMotion);
    }

    [Fact]
    public async Task Startup_and_feature_writers_share_one_sequential_snapshot()
    {
        var fixture = FeatureFixture.Create();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            Path.Combine(Path.GetTempPath(), "dudu-shared-preferences-" + Guid.NewGuid().ToString("N")),
            new FakeStartupWriter());
        var startupSettings = new StartupSettingsService(startup, fixture.Context.PreferenceMutations);
        var appearance = new AppearanceViewModel(fixture.Context) { Theme = AppTheme.Dark };

        await startupSettings.SetLaunchAtSignInAsync(false, TestContext.Current.CancellationToken);
        await appearance.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Same(fixture.Context.PreferenceMutations, startupSettings.PreferenceMutations);
        Assert.False(fixture.Context.CurrentPreferences.LaunchAtSignIn);
        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
    }

    [Fact]
    public async Task Concurrent_startup_and_feature_writes_do_not_lose_fields()
    {
        var fixture = FeatureFixture.Create();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            Path.Combine(Path.GetTempPath(), "dudu-concurrent-preferences-" + Guid.NewGuid().ToString("N")),
            new FakeStartupWriter());
        var startupSettings = new StartupSettingsService(startup, fixture.Context.PreferenceMutations);
        var appearance = new AppearanceViewModel(fixture.Context)
        {
            Theme = AppTheme.Dark,
            ReducedMotion = true,
        };

        await Task.WhenAll(
            startupSettings.SetLaunchAtSignInAsync(false, TestContext.Current.CancellationToken),
            appearance.SaveAsync(TestContext.Current.CancellationToken));

        Assert.False(fixture.Context.CurrentPreferences.LaunchAtSignIn);
        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.True(fixture.Context.CurrentPreferences.ReducedMotion);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
    }

    [Fact]
    public async Task Concurrent_startup_and_default_reminder_writes_share_the_same_owner()
    {
        var fixture = FeatureFixture.Create();
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe",
            Path.Combine(Path.GetTempPath(), "dudu-concurrent-defaults-" + Guid.NewGuid().ToString("N")),
            new FakeStartupWriter());
        var startupSettings = new StartupSettingsService(startup, fixture.Context.PreferenceMutations);
        var reminders = new RemindersViewModel(fixture.Context)
        {
            HydrationRemindersEnabled = false,
            BreakRemindersEnabled = true,
        };

        await Task.WhenAll(
            startupSettings.SetLaunchAtSignInAsync(false, TestContext.Current.CancellationToken),
            reminders.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken));

        Assert.False(fixture.Context.CurrentPreferences.LaunchAtSignIn);
        Assert.False(fixture.Context.CurrentPreferences.HydrationRemindersEnabled);
        Assert.True(fixture.Context.CurrentPreferences.BreakRemindersEnabled);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
        Assert.Equal(2, fixture.Reminders.Items.Count(item =>
            item.Id is "default-hydration" or "default-break"));
    }

    [Fact]
    public async Task Focus_end_uses_one_shot_acknowledgment_and_restores_idle()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);

        await viewModel.EndFocusAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            fixture.OneShotPresentations,
            presentation => presentation.Event is PetEvent.FocusEnded && presentation.DismissalId == "focus-end");
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
        Assert.Equal(FocusStatus.EndedEarly, viewModel.ActiveFocus!.Status);
    }

    [Fact]
    public async Task Ending_a_focus_session_refreshes_the_on_screen_history_without_navigating_away()
    {
        // Audit regression: FocusHistory was only ever populated by
        // RefreshAsync (Page_Loaded), so ending a session here left the
        // on-screen history stale until she navigated away and back.
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);

        await viewModel.EndFocusAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(viewModel.FocusHistory);
        Assert.Equal("ended early", entry.StatusText);
    }

    [Fact]
    public async Task Ending_a_focus_session_still_reports_success_when_the_history_reload_fails()
    {
        // Audit regression: the FocusHistory reload used to run inside the
        // same RunAsync lambda as the already-completed EndAsync call, so a
        // transient failure reading history (e.g. a repository IOException)
        // surfaced as "cannot finish that try again" even though the session
        // had genuinely ended. The reload is best-effort and must not turn a
        // successful end into a reported failure.
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        fixture.FocusSessions.ThrowOnListHistory = true;

        await viewModel.EndFocusAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(FocusStatus.EndedEarly, viewModel.ActiveFocus!.Status);
    }

    [Fact]
    public async Task Focus_history_shows_friendly_status_text_and_local_time()
    {
        // Regression: the history list used to bind straight to the raw FocusSession, showing
        // the bare enum name (e.g. "EndedEarly") and an unconverted UTC timestamp.
        var fixture = FeatureFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var startedUtc = DateTimeOffset.Parse("2026-09-12T10:30:00Z");
        var session = new FocusSession(
            Guid.NewGuid(), null, startedUtc, null, TimeSpan.Zero, FocusStatus.EndedEarly, startedUtc);
        await fixture.FocusSessions.SaveAsync(session, ct);
        var viewModel = new TasksFocusViewModel(fixture.Context);

        await viewModel.RefreshAsync(ct);

        var entry = Assert.Single(viewModel.FocusHistory);
        Assert.Equal("ended early", entry.StatusText);
        Assert.Equal(startedUtc.ToLocalTime().ToString("g"), entry.StartedText);
    }

    [Fact]
    public async Task Focus_expiring_naturally_refreshes_the_page_while_attached()
    {
        // Audit regression: FocusService.SessionExpired (raised from the background
        // reminder tick when a session runs out, not a manual "end focus") only ever
        // routed to the pet. If the Tasks & Focus page was open it kept showing the
        // session as running and its history list stayed stale until she navigated
        // away and back.
        //
        // Opus review follow-up 2: this reload must not clobber shared page state
        // with no user action behind it -- it must not clear an error banner she
        // is currently reading, and must not toggle IsBusy while a real user
        // command might be running. Only ActiveFocus/FocusHistory should move.
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        var started = await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        viewModel.AttachFocusExpiry();

        // An error banner she is currently reading, unrelated to focus.
        viewModel.RequestDeleteTaskCommand.Execute(null);
        Assert.Equal("select one first", viewModel.ErrorMessage);
        var busyChanged = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TasksFocusViewModel.IsBusy)) busyChanged = true;
        };

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(26);
        var completed = await fixture.Context.FocusService.CompleteExpiredAsync(
            started.Id, TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.False(viewModel.IsFocusActive);
        var entry = Assert.Single(viewModel.FocusHistory);
        Assert.Equal("completed", entry.StatusText);
        Assert.False(viewModel.IsBusy);
        Assert.False(busyChanged);
        Assert.Equal("select one first", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Detaching_focus_expiry_stops_the_automatic_refresh()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        var started = await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        viewModel.AttachFocusExpiry();
        viewModel.DetachFocusExpiry();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(26);

        var completed = await fixture.Context.FocusService.CompleteExpiredAsync(
            started.Id, TestContext.Current.CancellationToken);

        Assert.True(completed);
        // Nothing refreshed the view model, so it still shows the pre-completion snapshot.
        Assert.True(viewModel.IsFocusActive);
        Assert.Empty(viewModel.FocusHistory);
    }

    [Fact]
    public async Task Focus_expiry_refresh_failure_does_not_escape_the_session_expired_event()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        var started = await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        viewModel.AttachFocusExpiry();
        fixture.FocusSessions.ThrowOnListHistory = true;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(26);

        // A throwing history repository must not propagate out of FocusService's
        // SessionExpired invocation -- that would break the reminder tick for every
        // other subscriber (e.g. the pet).
        var completed = await fixture.Context.FocusService.CompleteExpiredAsync(
            started.Id, TestContext.Current.CancellationToken);

        Assert.True(completed);
        // Opus review follow-up 2: the quiet reload never goes through
        // RunAsync, so a failure here must stay silent -- not surface as an
        // ErrorMessage the user never asked for.
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Focus_expiry_reload_in_flight_does_not_clobber_a_session_started_meanwhile()
    {
        // Round 4 Opus follow-up 4: OnFocusSessionExpired used to ignore which
        // session expired, so a reload it queued for the OLD session could
        // still land after a NEW session was started in the meantime and wipe
        // it out with a stale (or null) snapshot fetched before the new
        // session existed.
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context);
        var sessionA = await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        viewModel.AttachFocusExpiry();

        var pauseHistory = new TaskCompletionSource<bool>();
        fixture.FocusSessions.PauseListHistory = pauseHistory;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(26);

        // CompleteExpiredAsync raises SessionExpired synchronously. The
        // fire-and-forget reload it starts runs through GetCurrentAsync --
        // session A just completed, so it captures a null focus -- and then
        // blocks inside ListHistoryAsync on pauseHistory, so it is
        // genuinely in flight (not finished) once this await returns.
        var completed = await fixture.Context.FocusService.CompleteExpiredAsync(
            sessionA.Id, TestContext.Current.CancellationToken);
        Assert.True(completed);

        // Start a new session while the stale reload for session A is still
        // paused mid-flight.
        var sessionB = await viewModel.StartFocusOrThrowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sessionB.Id, viewModel.ActiveFocus?.Id);

        // Let the paused reload run to completion.
        pauseHistory.SetResult(true);
        await pauseHistory.Task;
        // Give the resumed reload's continuation a chance to run before asserting.
        for (var i = 0; i < 5 && viewModel.FocusHistory.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Session B must survive: the reload was for session A, which is no
        // longer the active session, so it must not have touched ActiveFocus.
        Assert.Equal(sessionB.Id, viewModel.ActiveFocus?.Id);
        Assert.True(viewModel.IsFocusActive);
        // History still reloads unconditionally -- session A now shows up
        // there.
        Assert.Contains(viewModel.FocusHistory, entry => entry.StatusText == "completed");
    }

    [Theory]
    [InlineData(true, false, "another focus session is active")]
    [InlineData(false, true, "injected focus repository failure")]
    public async Task Start_focus_failure_keeps_action_surface_open_and_does_not_navigate(
        bool rejectCreate,
        bool throwOnCreate,
        string expectedError)
    {
        var fixture = FeatureFixture.Create();
        fixture.FocusSessions.RejectCreate = rejectCreate;
        fixture.FocusSessions.ThrowOnCreate = throwOnCreate;
        var destinations = new List<string>();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (destination, _) =>
            {
                destinations.Add(destination);
                return Task.CompletedTask;
            });
        var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var start = surface.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.StartFocus);

        await surface.HandlePointerAsync(Center(start.HitRegion), TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Primary, surface.Kind);
        Assert.Contains(expectedError, surface.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(destinations);
    }

    [Fact]
    public async Task Start_focus_navigates_and_closes_only_after_repository_success()
    {
        var fixture = FeatureFixture.Create();
        var destinations = new List<string>();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (destination, _) =>
            {
                Assert.NotNull(fixture.FocusSessions.Active);
                destinations.Add(destination);
                return Task.CompletedTask;
            });
        var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var start = surface.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.StartFocus);

        await surface.HandlePointerAsync(Center(start.HitRegion), TestContext.Current.CancellationToken);

        Assert.NotNull(fixture.FocusSessions.Active);
        Assert.Equal(["tasks"], destinations);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
        Assert.Null(surface.ErrorMessage);
    }

    [Fact]
    public async Task Preference_save_failure_never_publishes_or_applies_false_state()
    {
        var fixture = FeatureFixture.Create();
        var original = fixture.Context.CurrentPreferences;
        fixture.Preferences.FailNextSave = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Context.UpdatePreferencesAsync(
            current => current with { Theme = AppTheme.Dark },
            TestContext.Current.CancellationToken));

        Assert.Equal(original, fixture.Context.CurrentPreferences);
        Assert.Equal(original, fixture.RuntimePreferences.Current);
        Assert.Empty(fixture.RuntimePreferences.ApplyHistory);
    }

    [Fact]
    public async Task Preference_apply_failure_compensates_storage_and_runtime_before_rethrowing()
    {
        var fixture = FeatureFixture.Create();
        var original = fixture.Context.CurrentPreferences;
        fixture.RuntimePreferences.FailNextApply = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.UpdatePreferencesAsync(
            current => current with { Theme = AppTheme.Dark },
            TestContext.Current.CancellationToken));

        Assert.Equal(original, fixture.Context.CurrentPreferences);
        Assert.Equal(original, fixture.Preferences.Current);
        Assert.Equal(original, fixture.RuntimePreferences.Current);
        Assert.Equal(2, fixture.Preferences.SaveHistory.Count);
        Assert.Equal(2, fixture.RuntimePreferences.ApplyHistory.Count);
    }

    [Fact]
    public async Task Concurrent_preference_updates_serialize_without_losing_fields()
    {
        var fixture = FeatureFixture.Create();

        await Task.WhenAll(
            fixture.Context.UpdatePreferencesAsync(
                current => current with { Theme = AppTheme.Dark },
                TestContext.Current.CancellationToken),
            fixture.Context.UpdatePreferencesAsync(
                current => current with { ReducedMotion = true },
                TestContext.Current.CancellationToken));

        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.True(fixture.Context.CurrentPreferences.ReducedMotion);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.RuntimePreferences.Current);
    }

    [Fact]
    public async Task Restore_reloads_preferences_after_the_database_replacement()
    {
        var fixture = FeatureFixture.Create(restoreAsync: _ => Task.CompletedTask);
        fixture.Preferences.Current = Preferences.Default with { Theme = AppTheme.Dark };

        await fixture.Context.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
    }

    [Fact]
    public async Task Local_deletion_reloads_clean_default_preferences()
    {
        var fixture = FeatureFixture.Create(deleteLocalDataAsync: _ => Task.CompletedTask);
        fixture.Preferences.Current = null;

        await fixture.Context.DeleteLocalDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Preferences.Default, fixture.Context.CurrentPreferences);
    }

    [Fact]
    public async Task Restore_serializes_concurrent_preference_edits_and_reapplies_restored_state()
    {
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = Preferences.Default with { Theme = AppTheme.Dark, LocalNoteDailyLimit = 7 };
        FeatureFixture? fixture = null;
        fixture = FeatureFixture.Create(restoreAsync: async _ =>
        {
            fixture!.Preferences.Current = restored;
            restoreEntered.TrySetResult();
            await releaseRestore.Task;
        });

        var restore = fixture.Context.RestoreAsync(TestContext.Current.CancellationToken);
        await restoreEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var edit = fixture.Context.UpdatePreferencesAsync(
            current => current with { ReducedMotion = true },
            TestContext.Current.CancellationToken);
        Assert.False(edit.IsCompleted);

        releaseRestore.TrySetResult();
        await restore;
        await edit;

        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.Equal(7, fixture.Context.CurrentPreferences.LocalNoteDailyLimit);
        Assert.True(fixture.Context.CurrentPreferences.ReducedMotion);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.RuntimePreferences.Current);
    }

    [Fact]
    public async Task Editing_a_reminder_uses_its_stored_timezone_for_next_due()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            SelectedReminder = fixture.Reminder with
            {
                Rule = new RecurrenceRule.Daily(new TimeOnly(9, 0)),
                LocalTimeZoneId = "UTC",
            },
        };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"),
            fixture.Reminders.Items.Single(item => item.Id == fixture.Reminder.Id).NextDueUtc);
    }

    [Fact]
    public async Task Reminder_default_commit_precedes_runtime_publish_and_failure_keeps_old_state()
    {
        var fixture = FeatureFixture.Create();
        var original = fixture.Context.CurrentPreferences;
        fixture.Transactions.FailNextReminderCommit = true;
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            HydrationRemindersEnabled = true,
            BreakRemindersEnabled = true,
        };

        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal(original, fixture.Context.CurrentPreferences);
        Assert.Equal(original, fixture.RuntimePreferences.Current);
        Assert.DoesNotContain(fixture.Reminders.Items, item =>
            item.Id is "default-hydration" or "default-break");

        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Context.CurrentPreferences.HydrationRemindersEnabled);
        Assert.True(fixture.Context.CurrentPreferences.BreakRemindersEnabled);
        Assert.Equal(2, fixture.Reminders.Items.Count(item =>
            item.Id is "default-hydration" or "default-break"));
    }

    [Fact]
    public async Task Reminder_evening_and_bedtime_routines_opt_in_and_upsert_stably()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new RemindersViewModel(fixture.Context);

        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Context.CurrentPreferences.EveningCheckInEnabled);
        Assert.False(fixture.Context.CurrentPreferences.BedtimeRitualEnabled);
        Assert.All(fixture.Reminders.Items.Where(item =>
            item.Id is Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId
                or Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId), item =>
            Assert.False(item.Enabled));

        viewModel.EveningCheckInEnabled = true;
        viewModel.BedtimeRitualEnabled = true;
        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Context.CurrentPreferences.EveningCheckInEnabled);
        Assert.True(fixture.Context.CurrentPreferences.BedtimeRitualEnabled);
        var evening = fixture.Reminders.Items.Single(item =>
            item.Id == Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId);
        var bedtime = fixture.Reminders.Items.Single(item =>
            item.Id == Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId);
        Assert.Equal("how was your day?", evening.Title);
        Assert.Equal("shuijiaojiao", bedtime.Title);
        Assert.Equal(new RecurrenceRule.Daily(new TimeOnly(20, 0)), evening.Rule);
        Assert.Equal(new RecurrenceRule.Daily(new TimeOnly(22, 0)), bedtime.Rule);
        Assert.All(new[] { evening, bedtime }, item =>
        {
            Assert.Null(item.QuietHours);
            Assert.Equal(QuietHoursBehavior.WaitUntilQuietHoursEnd, item.QuietHoursBehavior);
            Assert.Equal(MissedOccurrencePolicy.Skip, item.MissedPolicy);
            Assert.Equal("UTC", item.LocalTimeZoneId);
        });

        viewModel.EveningCheckInEnabled = false;
        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, fixture.Reminders.Items.Count(item =>
            item.Id is "default-hydration" or "default-break"
                or Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId
                or Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId));
        Assert.False(fixture.Reminders.Items.Single(item =>
            item.Id == Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId).Enabled);
    }

    [Fact]
    public async Task Saving_reminder_preferences_preserves_next_due_and_snooze_for_an_unchanged_default()
    {
        // Audit regression: every save rebuilt all four defaults from scratch via
        // LocalReminderDefaults.Create, wiping NextDueUtc/SnoozedUntilUtc even for
        // a default whose own enabled-state and schedule this save never touched.
        var fixture = FeatureFixture.Create();
        var previous = fixture.Reminder with
        {
            Id = "default-hydration",
            Enabled = true,
            Rule = new RecurrenceRule.Daily(new TimeOnly(10, 0)),
            NextDueUtc = DateTimeOffset.Parse("2026-09-12T11:45:00Z"),
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        fixture.Reminders.Items.Add(previous);
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            HydrationRemindersEnabled = true,
            BreakRemindersEnabled = true,
        };

        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        var saved = fixture.Reminders.Items.Single(item => item.Id == "default-hydration");
        Assert.Equal(previous.NextDueUtc, saved.NextDueUtc);
        Assert.Equal(previous.SnoozedUntilUtc, saved.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Reminder_runtime_apply_failure_restores_exact_previous_default_rows()
    {
        var fixture = FeatureFixture.Create();
        var previous = fixture.Reminder with
        {
            Id = "default-hydration",
            Title = "My water schedule",
            SnoozedUntilUtc = DateTimeOffset.Parse("2026-09-12T11:30:00Z"),
        };
        fixture.Reminders.Items.Add(previous);
        fixture.RuntimePreferences.FailNextApply = true;
        var viewModel = new RemindersViewModel(fixture.Context)
        {
            HydrationRemindersEnabled = false,
            BreakRemindersEnabled = true,
        };

        await viewModel.SaveReminderPreferencesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.RuntimePreferences.Current);
        Assert.Equal(previous, fixture.Reminders.Items.Single(item => item.Id == "default-hydration"));
        Assert.DoesNotContain(fixture.Reminders.Items, item => item.Id == "default-break");
    }

    [Fact]
    public async Task Remote_note_transaction_failure_does_not_mutate_view_model_state()
    {
        var fixture = FeatureFixture.Create();
        var envelope = new RemoteEnvelope("atomic-note", [1], fixture.Clock.UtcNow);
        fixture.RemoteNotes.Pending.Add(envelope);
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await viewModel.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);
        fixture.Transactions.FailNextRemoteCommit = true;

        await viewModel.SaveOpenedNoteAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Contains(envelope, viewModel.PendingRemoteNotes);
        Assert.Equal(envelope, viewModel.OpenedRemoteEnvelope);
    }

    [Fact]
    public async Task Reveal_remote_note_maps_reaction_to_one_shot_pet_presentation_per_ruling()
    {
        async Task<(PetEvent Event, string DismissalId)?> RevealWithReactionAsync(string reaction)
        {
            var fixture = FeatureFixture.Create(
                revealRemoteNoteAsync: (_, _) => Task.FromResult(new RevealedRemoteNote("hi", reaction)));
            var envelope = new RemoteEnvelope($"note-{reaction}", [1], fixture.Clock.UtcNow);
            fixture.RemoteNotes.Pending.Add(envelope);
            var viewModel = new LoveNotesViewModel(fixture.Context);
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

            await viewModel.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);

            return fixture.OneShotPresentations.Count == 0
                ? null
                : Assert.Single(fixture.OneShotPresentations);
        }

        var wave = await RevealWithReactionAsync("wave");
        Assert.Equal("greeting", Assert.IsType<PetEvent.AmbientRequested>(wave!.Value.Event).AnimationKey);
        Assert.Equal("greeting", wave.Value.DismissalId);

        var heart = await RevealWithReactionAsync("heart");
        Assert.Equal("note-heart", Assert.IsType<PetEvent.RemoteNoteArrived>(heart!.Value.Event).MessageId);
        Assert.Equal("note-heart", heart.Value.DismissalId);

        var hug = await RevealWithReactionAsync("hug");
        Assert.IsType<PetEvent.ComfortRequested>(hug!.Value.Event);
        Assert.Equal("comfort-hug", hug.Value.DismissalId);

        var celebrate = await RevealWithReactionAsync("celebrate");
        Assert.Equal("celebrate", Assert.IsType<PetEvent.AmbientRequested>(celebrate!.Value.Event).AnimationKey);
        Assert.Equal("celebrate", celebrate.Value.DismissalId);

        Assert.Null(await RevealWithReactionAsync("none"));
    }

    [Fact]
    public async Task Reveal_dismisses_unread_indicator_for_every_reaction()
    {
        async Task AssertRevealDismissesUnreadIndicatorAsync(string reaction)
        {
            var fixture = FeatureFixture.Create(
                revealRemoteNoteAsync: (_, _) => Task.FromResult(new RevealedRemoteNote("hi", reaction)));
            var messageId = $"unread-{reaction}";
            var envelope = new RemoteEnvelope(messageId, [1], fixture.Clock.UtcNow);
            fixture.RemoteNotes.Pending.Add(envelope);
            var viewModel = new LoveNotesViewModel(fixture.Context);
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

            // Simulate the note-arrival presentation RemoteSyncService already raised when the
            // envelope arrived, so the pet's unread indicator is set before the user opens it.
            fixture.Context.Pet.Handle(new PetEvent.RemoteNoteArrived(messageId));
            Assert.Equal(PetState.RemoteNote, fixture.Context.Pet.Current.State);

            await viewModel.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);

            Assert.NotEqual(PetState.RemoteNote, fixture.Context.Pet.Current.State);
        }

        await AssertRevealDismissesUnreadIndicatorAsync("wave");
        await AssertRevealDismissesUnreadIndicatorAsync("hug");
        await AssertRevealDismissesUnreadIndicatorAsync("celebrate");
        await AssertRevealDismissesUnreadIndicatorAsync("none");
        await AssertRevealDismissesUnreadIndicatorAsync("heart");
    }

    [Fact]
    public async Task Countdown_supports_create_select_edit_and_delete()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new HomeViewModel(fixture.Context)
        {
            CountdownTitle = "Anniversary",
            CountdownTargetUtc = DateTimeOffset.Parse("2026-12-01T12:00:00Z"),
        };
        await viewModel.SaveCountdownAsync(TestContext.Current.CancellationToken);
        var countdown = Assert.Single(fixture.Countdowns.Items);

        viewModel.SelectCountdown(countdown);
        viewModel.CountdownTitle = "Our anniversary";
        viewModel.CountdownTargetUtc = DateTimeOffset.Parse("2026-12-02T12:00:00Z");
        await viewModel.SaveCountdownAsync(TestContext.Current.CancellationToken);

        var updated = Assert.Single(fixture.Countdowns.Items);
        Assert.Equal(countdown.Id, updated.Id);
        Assert.Equal("Our anniversary", updated.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-12-02T12:00:00Z"), updated.TargetUtc);

        await viewModel.DeleteCountdownAsync(updated, TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Countdowns.Items);
        Assert.Null(viewModel.SelectedCountdown);
    }

    [Fact]
    public async Task Tasks_support_due_date_edit_completion_and_deletion()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new TasksFocusViewModel(fixture.Context)
        {
            Title = "Book dinner",
            DueUtc = DateTimeOffset.Parse("2026-09-20T18:00:00Z"),
        };
        await viewModel.SaveTaskAsync(TestContext.Current.CancellationToken);
        var task = Assert.Single(fixture.Tasks.Items);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T18:00:00Z"), task.DueUtc);

        viewModel.SelectTask(task);
        viewModel.Title = "Book birthday dinner";
        viewModel.DueUtc = DateTimeOffset.Parse("2026-09-21T18:00:00Z");
        await viewModel.SaveTaskAsync(TestContext.Current.CancellationToken);
        var updated = Assert.Single(fixture.Tasks.Items);
        Assert.Equal(task.Id, updated.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T18:00:00Z"), updated.DueUtc);

        await viewModel.CompleteTaskAsync(updated, TestContext.Current.CancellationToken);
        var completed = Assert.Single(fixture.Tasks.Items);
        Assert.True(completed.IsCompleted);

        await viewModel.DeleteTaskAsync(completed, TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Tasks.Items);
    }

    [Fact]
    public async Task Action_surface_toggles_from_pet_and_routes_primary_and_comfort_hits()
    {
        var fixture = FeatureFixture.Create();
        var destinations = new List<string>();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (destination, _) =>
            {
                destinations.Add(destination);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);
        var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        var workArea = new PixelRect(0, 0, 640, 480);
        var anchor = new Dudu.Core.Assets.PixelPoint(320, 400);

        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
        surface.ToggleFromPetBody(workArea, anchor);
        Assert.Equal(OverlayActionSurfaceKind.Primary, surface.Kind);
        var comfort = surface.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.ComfortMe);
        await surface.HandlePointerAsync(Center(comfort.HitRegion), TestContext.Current.CancellationToken);
        Assert.Equal(OverlayActionSurfaceKind.Comfort, surface.Kind);
        Assert.All(surface.ComfortArrangement!.Actions, item =>
            Assert.True(surface.ComfortArrangement.Bounds.Contains(item.HitRegion)));
        var close = surface.ComfortArrangement.Actions.Single(item => item.Action == ComfortAction.Close);
        await surface.HandlePointerAsync(Center(close.HitRegion), TestContext.Current.CancellationToken);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);

        surface.ToggleFromPetBody(workArea, anchor);
        var tasks = surface.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.Tasks);
        await surface.HandlePointerAsync(Center(tasks.HitRegion), TestContext.Current.CancellationToken);
        Assert.Equal(["tasks"], destinations);
        Assert.Equal(OverlayActionSurfaceKind.Closed, surface.Kind);
    }

    [Fact]
    public async Task Action_surface_keeps_failed_navigation_visible_with_an_error()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.FromException(new InvalidOperationException("navigation failed")));
        var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(
            new PixelRect(0, 0, 640, 480),
            new Dudu.Core.Assets.PixelPoint(320, 400));
        var tasks = surface.Arrangement!.PrimaryActions.Single(item => item.Action == OverlayAction.Tasks);

        await surface.HandlePointerAsync(Center(tasks.HitRegion), TestContext.Current.CancellationToken);

        Assert.Equal(OverlayActionSurfaceKind.Primary, surface.Kind);
        Assert.Equal("navigation failed", surface.ErrorMessage);
    }

    private static Dudu.Core.Assets.PixelPoint Center(PixelRect rectangle) =>
        new(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);

    private sealed class FeatureFixture
    {
        private FeatureFixture(
            FakeClock clock,
            FakeReminderRepository reminders,
            FakeLocalNoteRepository localNotes,
            FakeRemoteEnvelopeRepository remoteNotes,
            FakePreferencesRepository preferences,
            FakePlacementRepository placements,
            FakeProfileRepository profiles,
            FakeTaskRepository tasks,
            FakeFocusRepository focusSessions,
            FakeCountdownRepository countdowns,
            FakeFeatureTransactions transactions,
            FakeRuntimePreferences runtimePreferences,
            CompanionFeatureContext context,
            List<string> events,
            List<(PetEvent Event, string DismissalId)> oneShotPresentations)
        {
            Clock = clock;
            Reminders = reminders;
            LocalNotes = localNotes;
            RemoteNotes = remoteNotes;
            Preferences = preferences;
            Placements = placements;
            Profiles = profiles;
            Tasks = tasks;
            FocusSessions = focusSessions;
            Countdowns = countdowns;
            Transactions = transactions;
            RuntimePreferences = runtimePreferences;
            Context = context;
            Events = events;
            OneShotPresentations = oneShotPresentations;
        }

        public FakeClock Clock { get; }
        public FakeReminderRepository Reminders { get; }
        public FakeLocalNoteRepository LocalNotes { get; }
        public FakeRemoteEnvelopeRepository RemoteNotes { get; }
        public FakePreferencesRepository Preferences { get; }
        public FakePlacementRepository Placements { get; }
        public FakeProfileRepository Profiles { get; }
        public FakeTaskRepository Tasks { get; }
        public FakeFocusRepository FocusSessions { get; }
        public FakeCountdownRepository Countdowns { get; }
        public FakeFeatureTransactions Transactions { get; }
        public FakeRuntimePreferences RuntimePreferences { get; }
        public CompanionFeatureContext Context { get; }
        public List<string> Events { get; }
        public List<(PetEvent Event, string DismissalId)> OneShotPresentations { get; }
        public Reminder Reminder { get; } = new(
            "reminder-1",
            "Drink water",
            null,
            true,
            new RecurrenceRule.Once(),
            "UTC",
            QuietHoursBehavior.WaitUntilQuietHoursEnd,
            MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"));

        public static FeatureFixture Create(
            Func<CancellationToken, Task>? restoreAsync = null,
            Func<CancellationToken, Task>? deleteLocalDataAsync = null,
            Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
            Func<RemoteEnvelope, CancellationToken, Task<RevealedRemoteNote>>? revealRemoteNoteAsync = null,
            IPairingService? pairing = null,
            Func<string, CancellationToken, Task>? discardHeldReminderAsync = null,
            Func<string, CancellationToken, Task>? dismissReminderNotificationAsync = null)
        {
            var clock = new FakeClock("2026-09-12T10:00:00Z");
            var events = new List<string>();
            var oneShotPresentations = new List<(PetEvent Event, string DismissalId)>();
            var preferences = new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false,
                3,
                true,
                false,
                true,
                TimeSpan.FromMinutes(15));
            var preferenceRepository = new FakePreferencesRepository();
            var profileRepository = new FakeProfileRepository();
            var placementRepository = new FakePlacementRepository();
            var reminders = new FakeReminderRepository(events);
            var tasks = new FakeTaskRepository();
            var focusSessions = new FakeFocusRepository();
            var localNotes = new FakeLocalNoteRepository();
            var remoteNotes = new FakeRemoteEnvelopeRepository();
            var countdowns = new FakeCountdownRepository();
            var checkIns = new FakeCheckInRepository();
            var taskService = new TaskService(tasks, clock);
            var focusService = new FocusService(focusSessions, clock, tasks);
            var checkInService = new CheckInService(checkIns, clock);
            var noteSelector = new LocalNoteSelector(localNotes, clock, new FixedRandom(), preferences);
            var pause = PauseState.None;
            var transactions = new FakeFeatureTransactions(preferenceRepository, reminders, localNotes, remoteNotes);
            var runtimePreferences = new FakeRuntimePreferences(preferences);
            var preferenceMutations = new PreferenceMutationCoordinator(
                preferences,
                preferenceRepository,
                runtimePreferences.ApplyAsync);
            var pet = PetStateMachine.CreateIdle();
            var context = new CompanionFeatureContext(
                clock,
                preferenceMutations,
                profileRepository,
                placementRepository,
                reminders,
                reminders,
                tasks,
                focusSessions,
                localNotes,
                remoteNotes,
                countdowns,
                checkIns,
                checkInService,
                taskService,
                focusService,
                noteSelector,
                pairing ?? new FakePairing(),
                transactions,
                pet,
                getPauseState: () => pause,
                applyPauseAsync: (state, _) =>
                {
                    pause = state;
                    return Task.CompletedTask;
                },
                presentPetAsync: (petEvent, _) =>
                {
                    pet.Handle(petEvent);
                    events.Add(petEvent switch
                    {
                        PetEvent.Dismissed => "pet.dismiss",
                        PetEvent.FocusEnded => "pet.focus-end",
                        _ => "pet.present",
                    });
                    return Task.CompletedTask;
                },
                presentOneShotPetAsync: (petEvent, dismissalId, token) =>
                {
                    oneShotPresentations.Add((petEvent, dismissalId));
                    pet.Handle(petEvent);
                    pet.Handle(new PetEvent.PresentationAcknowledged());
                    pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
                    return Task.CompletedTask;
                },
                revealRemoteNoteAsync: revealRemoteNoteAsync
                    ?? ((_, _) => Task.FromResult(new RevealedRemoteNote("You can do it", "none"))),
                restoreAsync: restoreAsync,
                deleteLocalDataAsync: deleteLocalDataAsync,
                deleteRemoteDataAsync: deleteRemoteDataAsync,
                discardHeldReminderAsync: discardHeldReminderAsync,
                dismissReminderNotificationAsync: dismissReminderNotificationAsync);
            return new FeatureFixture(
                clock,
                reminders,
                localNotes,
                remoteNotes,
                preferenceRepository,
                placementRepository,
                profileRepository,
                tasks,
                focusSessions,
                countdowns,
                transactions,
                runtimePreferences,
                context,
                events,
                oneShotPresentations);
        }
    }

    private sealed class FakeClock(string value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse(value);
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedRandom : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    private sealed class FakeReminderRepository(List<string> events) : IReminderRepository, IReminderWriter
    {
        public List<Reminder> Items { get; } = [];
        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Reminder>>(Items);
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Reminder>>(Items);
        public Task<bool> RecordOccurrencesAndAdvanceAsync(Reminder reminder, IReadOnlyList<ReminderOccurrence> occurrences, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken)
        {
            events.Add("repository.complete");
            return Task.FromResult(true);
        }
        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
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
        public List<LocalLoveNote> Enabled => Notes.Where(note => note.Enabled).ToList();
        public Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes);
        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalLoveNote>>(Enabled);
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

    private sealed class FakeRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public List<RemoteEnvelope> Pending { get; } = [];
        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult(Pending.FirstOrDefault(item => item.MessageId == messageId));
        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteEnvelope>>(Pending);
        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) { Pending.Add(envelope); return Task.FromResult(true); }
        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> TryConsumeAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken)
        {
            var removed = Pending.RemoveAll(item => item.MessageId == messageId) == 1;
            return Task.FromResult(removed);
        }
        public Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) { Pending.RemoveAll(item => item.MessageId == messageId); return Task.CompletedTask; }
        public Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) { var count = Pending.Count; Pending.Clear(); return Task.FromResult(count); }
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Preferences? Current { get; set; }
        public bool FailNextSave { get; set; }
        public List<Preferences> SaveHistory { get; } = [];
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("injected preference save failure");
            }
            Current = preferences;
            SaveHistory.Add(preferences);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public Profile? Current { get; set; }
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken)
        {
            Current = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlacementRepository : IPetPlacementRepository
    {
        private readonly Dictionary<string, PetPlacement> _items = [];
        public List<PetPlacement> SaveHistory { get; } = [];
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.FromResult(_items.GetValueOrDefault(monitorDeviceName));
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PetPlacement>>(_items.Values.ToArray());
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken)
        {
            _items[placement.MonitorDeviceName] = placement;
            SaveHistory.Add(placement);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) { _items.Remove(monitorDeviceName); return Task.CompletedTask; }
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        private readonly Dictionary<Guid, TaskItem> _tasks = [];
        public IReadOnlyCollection<TaskItem> Items => _tasks.Values;
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
        public FocusSession? Active => _sessions.Values.FirstOrDefault(
            item => item.Status is FocusStatus.Running or FocusStatus.Paused);
        public bool RejectCreate { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnListHistory { get; set; }
        // Round 4 Opus follow-up 4: lets a test suspend an in-flight expiry
        // reload right after it has already captured its (possibly stale)
        // GetCurrentAsync snapshot, so a new session can be started before
        // the reload's mutation finally runs.
        public TaskCompletionSource<bool>? PauseListHistory { get; set; }
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);
        public async Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnListHistory) throw new IOException("injected focus history repository failure");
            if (PauseListHistory is { } pause) await pause.Task;
            return _sessions.Values.Where(item => item.Status is not (FocusStatus.Running or FocusStatus.Paused)).ToArray();
        }
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken)
        {
            if (ThrowOnCreate) throw new IOException("injected focus repository failure");
            if (RejectCreate) return Task.FromResult(false);
            _sessions[session.Id] = session;
            return Task.FromResult(true);
        }
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken) { _sessions[expected.Id] = replacement; return Task.FromResult(true); }
        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken) { _sessions[session.Id] = session; return Task.CompletedTask; }
    }

    private sealed class FakeCountdownRepository : ICountdownRepository
    {
        private readonly Dictionary<string, Countdown> _items = [];
        public IReadOnlyCollection<Countdown> Items => _items.Values;
        public Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_items.GetValueOrDefault(id));
        public Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Countdown>>(_items.Values.ToArray());
        public Task SaveAsync(Countdown countdown, CancellationToken cancellationToken) { _items[countdown.Id] = countdown; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken cancellationToken) { _items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeCheckInRepository : ICheckInRepository
    {
        private readonly List<MoodCheckIn> _items = [];
        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken)
        {
            _items.Add(checkIn);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodCheckIn>>(_items.Where(item => item.CreatedUtc >= sinceUtc).ToArray());
    }

    private sealed class FakePairing : IPairingService
    {
        // Defaults preserve every pre-existing test's behavior (Offline, no per-call tracking).
        // F3 tests set State to Available where DisconnectSenderSessionsAsync/DeleteRemoteDeviceAsync
        // need to actually run (IPairingService's RevokeSessionsWithResultAsync/
        // DeleteRemoteDeviceWithResultAsync both refuse while Offline), and read the call counts to
        // prove the confirmation gate withholds the relay call until ConfirmAsync runs.
        public PairingAvailability State { get; set; } = PairingAvailability.Offline;
        public int DisconnectSenderSessionsCallCount { get; private set; }
        public int DeleteRemoteDeviceCallCount { get; private set; }
        public int ForgetPairingCallCount { get; private set; }

        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default)
        {
            DisconnectSenderSessionsCallCount++;
            return Task.CompletedTask;
        }
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default)
        {
            DeleteRemoteDeviceCallCount++;
            return Task.CompletedTask;
        }

        // F2: the local-only escape hatch. Never routes through GetStateAsync, so it must work
        // regardless of State -- unlike DisconnectSenderSessionsAsync/DeleteRemoteDeviceAsync above.
        public Task ForgetPairingAsync(CancellationToken cancellationToken = default)
        {
            ForgetPairingCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupWriter : IStartupLinkWriter
    {
        public Task WriteAtomicAsync(
            string shortcutPath,
            string targetPath,
            string arguments,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeFeatureTransactions(
        IPreferencesRepository preferences,
        FakeReminderRepository reminders,
        FakeLocalNoteRepository localNotes,
        FakeRemoteEnvelopeRepository remoteNotes) : ICompanionFeatureTransactions
    {
        public bool FailNextReminderCommit { get; set; }
        public bool FailNextRemoteCommit { get; set; }

        public async Task SavePreferencesAndDefaultRemindersAsync(
            Preferences value,
            DateTimeOffset nowUtc,
            TimeZoneInfo localTimeZone,
            CancellationToken cancellationToken = default)
        {
            if (FailNextReminderCommit)
            {
                FailNextReminderCommit = false;
                throw new IOException("injected reminder transaction failure");
            }
            await preferences.SaveAsync(value, cancellationToken);

            // Mirrors CompanionFeatureTransactionService.SavePreferencesAndDefaultRemindersAsync:
            // carry NextDueUtc/SnoozedUntilUtc over from the existing row when
            // nothing that determines the schedule actually changed, keep
            // Rule/QuietHoursBehavior/MissedPolicy from the existing row (they
            // are a hardcoded per-id default in Create, never derived from
            // Preferences), recompute NextDueUtc from that preserved rule when
            // re-enabling, and preserve a user-edited (or cleared) Title/Details.
            var existing = (await reminders.ListAsync(cancellationToken))
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var reminder in Dudu.Core.Reminders.LocalReminderDefaults.Create(
                value,
                nowUtc,
                localTimeZone))
            {
                var toSave = reminder;
                if (existing.TryGetValue(reminder.Id, out var previous))
                {
                    toSave = toSave with
                    {
                        Rule = previous.Rule,
                        QuietHoursBehavior = previous.QuietHoursBehavior,
                        MissedPolicy = previous.MissedPolicy,
                    };

                    if (previous.Enabled == reminder.Enabled
                        && previous.LocalTimeZoneId == reminder.LocalTimeZoneId)
                    {
                        toSave = toSave with
                        {
                            NextDueUtc = previous.NextDueUtc,
                            SnoozedUntilUtc = previous.SnoozedUntilUtc,
                        };
                    }
                    else if (reminder.Enabled)
                    {
                        // Re-enabling, or staying enabled with a changed
                        // LocalTimeZoneId: recompute from the preserved rule.
                        // No `?? reminder.NextDueUtc` fallback -- a preserved
                        // rule with no next occurrence (e.g. a completed
                        // Once-edited default) must stay null.
                        //
                        // Re-anchor on a full day before now, but ONLY for a
                        // wall-clock rule (Daily/SelectedWeekdays) whose zone
                        // actually changed -- see
                        // CompanionFeatureTransactionService.SavePreferencesAndDefaultRemindersAsync
                        // for the full reasoning: Once must never be
                        // cancelled by a tz change (NextOccurrence has no
                        // fallthrough case for it), Interval must not be
                        // needlessly pushed by up to one period, and "now"
                        // itself is unsafe as an anchor because a now inside
                        // quiet hours would land on quiet-hours end instead
                        // of the next wall-clock occurrence.
                        var reAnchorForZoneChange = previous.NextDueUtc is not null
                            && previous.LocalTimeZoneId != reminder.LocalTimeZoneId
                            && toSave.Rule is Dudu.Core.Models.RecurrenceRule.Daily
                                or Dudu.Core.Models.RecurrenceRule.SelectedWeekdays;
                        var anchor = reAnchorForZoneChange
                            ? nowUtc.ToUniversalTime().AddDays(-1)
                            : previous.NextDueUtc;
                        toSave = toSave with
                        {
                            NextDueUtc = Dudu.Core.Reminders.ReminderScheduler.NextOccurrence(
                                toSave with { NextDueUtc = anchor, SnoozedUntilUtc = null },
                                nowUtc.ToUniversalTime(),
                                localTimeZone),
                            SnoozedUntilUtc = null,
                        };
                    }
                    else
                    {
                        // Disabling: carry the real NextDueUtc forward so it
                        // doesn't get poisoned with Create's shipped value,
                        // which would become the anchor for a later re-enable.
                        toSave = toSave with { NextDueUtc = previous.NextDueUtc };
                    }

                    if (!Dudu.Core.Reminders.LocalReminderDefaults.IsKnownDefaultTitle(reminder.Id, previous.Title))
                    {
                        toSave = toSave with { Title = previous.Title };
                    }
                    if (!Dudu.Core.Reminders.LocalReminderDefaults.IsKnownDefaultDetails(reminder.Id, previous.Details ?? string.Empty))
                    {
                        toSave = toSave with { Details = previous.Details };
                    }
                }

                // See CompanionFeatureTransactionService.SavePreferencesAndDefaultRemindersAsync:
                // insurance against a recompute above returning null for a
                // non-Once rule, which would otherwise trip ValidateForSave.
                if (toSave.Enabled && toSave.NextDueUtc is null && toSave.Rule is not Dudu.Core.Models.RecurrenceRule.Once)
                {
                    toSave = toSave with { NextDueUtc = reminder.NextDueUtc };
                }

                await reminders.SaveAsync(toSave, cancellationToken);
            }
        }

        public async Task SaveRemoteNoteAndConsumeEnvelopeAsync(
            LocalLoveNote note,
            string messageId,
            DateTimeOffset processedUtc,
            CancellationToken cancellationToken = default)
        {
            if (FailNextRemoteCommit)
            {
                FailNextRemoteCommit = false;
                throw new IOException("injected remote transaction failure");
            }
            await localNotes.SaveToJarAsync(note, cancellationToken);
            if (!await remoteNotes.TryConsumeAsync(messageId, processedUtc, cancellationToken))
            {
                throw new InvalidOperationException("Remote note is unavailable.");
            }
        }

        public async Task RestorePreferencesAndDefaultRemindersAsync(
            Preferences value,
            IReadOnlyList<Reminder> previousDefaultReminders,
            CancellationToken cancellationToken = default)
        {
            await preferences.SaveAsync(value, cancellationToken);
            await reminders.DeleteAsync("default-hydration", cancellationToken);
            await reminders.DeleteAsync("default-break", cancellationToken);
            await reminders.DeleteAsync(Dudu.Core.Reminders.LocalReminderDefaults.EveningCheckInId, cancellationToken);
            await reminders.DeleteAsync(Dudu.Core.Reminders.LocalReminderDefaults.BedtimeId, cancellationToken);
            foreach (var reminder in previousDefaultReminders)
            {
                await reminders.SaveAsync(reminder, cancellationToken);
            }
        }
    }

    private sealed class FakeRuntimePreferences(Preferences initial)
    {
        public Preferences Current { get; private set; } = initial;
        public bool FailNextApply { get; set; }
        public List<Preferences> ApplyHistory { get; } = [];

        public Task ApplyAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            ApplyHistory.Add(preferences);
            if (FailNextApply)
            {
                FailNextApply = false;
                throw new InvalidOperationException("injected runtime apply failure");
            }
            Current = preferences;
            return Task.CompletedTask;
        }
    }
}
