using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Dudu.App.Overlay;
using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class FeatureViewModelTests
{
    [Fact]
    public void Home_exposes_no_practical_members()
    {
        var names = typeof(HomeViewModel).GetProperties().Select(p => p.Name)
            .Concat(typeof(HomeViewModel).GetMethods().Select(m => m.Name))
            .ToArray();

        foreach (var banned in new[] { "Reminder", "Focus", "Countdown", "CheckIn", "Mood", "Pause", "Task" })
        {
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Home_greeting_takes_the_saved_recipient_name_instead_of_a_hardcoded_one()
    {
        var fixture = FeatureFixture.Create();
        fixture.Profiles.Current = new Profile("mei", OnboardingComplete: true);
        var viewModel = new HomeViewModel(fixture.Context);
        var announced = new List<string?>();
        viewModel.PropertyChanged += (_, args) => announced.Add(args.PropertyName);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hi hi, mei! dudu missed u", viewModel.GreetingText);
        Assert.Contains(nameof(HomeViewModel.GreetingText), announced);
    }

    [Fact]
    public async Task Home_greeting_trims_a_saved_recipient_name_with_stray_whitespace()
    {
        // A name saved with stray surrounding whitespace must not leak into Home's
        // copy as an extra space before the name or before the punctuation.
        var fixture = FeatureFixture.Create();
        fixture.Profiles.Current = new Profile("  mei  ", OnboardingComplete: true);
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hi hi, mei! dudu missed u", viewModel.GreetingText);
    }

    [Fact]
    public async Task Home_greeting_drops_the_name_clause_naturally_when_no_recipient_name_is_saved()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new HomeViewModel(fixture.Context);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hi hi! dudu missed u", viewModel.GreetingText);
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
    public async Task An_error_hides_an_earlier_success_message_so_both_never_show_together()
    {
        // The request-delete handlers set ErrorMessage directly, so an earlier
        // "otayyy opened" and "select one first" could show side by side.
        var fixture = FeatureFixture.Create();
        var notes = await RevealOneAsync(fixture, "m-1");
        Assert.True(notes.HasStatus);
        notes.RequestDeleteOpenedNoteCommand.Execute(null);
        Assert.True(notes.HasError);
        Assert.False(notes.HasStatus);

        notes.RequestDeleteOpenedNoteCommand.Execute(Assert.Single(notes.OpenedNotes));
        Assert.False(notes.HasError);
        Assert.False(notes.HasStatus);
    }

    [Fact]
    public async Task Null_selection_reports_select_one_first_instead_of_internals()
    {
        var fixture = FeatureFixture.Create();
        var notes = new LoveNotesViewModel(fixture.Context);
        await notes.DeleteOpenedNoteCommand.ExecuteAsync(null);
        Assert.Equal("select one first", notes.ErrorMessage);
        await notes.RevealRemoteNoteCommand.ExecuteAsync(null);
        Assert.Equal("select one first", notes.ErrorMessage);
    }

    [Fact]
    public void ArgumentException_error_text_strips_the_framework_parameter_suffix()
    {
        // Regression: ArgumentException.Message appends " (Parameter 'name')" from
        // ParamName. That framework wording leaked straight into the user-visible error text.
        var exception = new ArgumentException("wait type a name first", "RecipientName");

        var message = InvokeToUserMessage(exception);

        Assert.Equal("wait type a name first", message);
        Assert.DoesNotContain("Parameter", message);
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
        // H-2: the RequestDelete...Commands used to be silent no-ops with a null
        // selection (the button that runs them stays enabled with nothing selected).
        var fixture = FeatureFixture.Create();

        var notes = new LoveNotesViewModel(fixture.Context);
        notes.RequestDeleteOpenedNoteCommand.Execute(null);
        Assert.Equal("select one first", notes.ErrorMessage);
        Assert.False(notes.IsConfirmingDeleteNote);
    }

    [Fact]
    public async Task Opened_note_deletion_requires_a_separate_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = await RevealOneAsync(fixture, "m-1");
        var note = Assert.Single(fixture.LocalNotes.Notes);

        viewModel.RequestDeleteOpenedNoteCommand.Execute(note);
        Assert.True(viewModel.IsConfirmingDeleteNote);
        Assert.Equal(note, viewModel.PendingDeleteNote);
        Assert.Single(fixture.LocalNotes.Notes);

        viewModel.CancelDeleteOpenedNoteCommand.Execute(null);
        Assert.False(viewModel.IsConfirmingDeleteNote);
        Assert.Single(fixture.LocalNotes.Notes);

        viewModel.RequestDeleteOpenedNoteCommand.Execute(note);
        await viewModel.DeleteOpenedNoteCommand.ExecuteAsync(viewModel.PendingDeleteNote);

        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Empty(viewModel.OpenedNotes);
        Assert.False(viewModel.IsConfirmingDeleteNote);
    }

    [Fact]
    public async Task Deleting_an_opened_note_matches_by_id_even_if_the_caller_passes_a_stale_copy()
    {
        // L-2: LocalLoveNote is a record, so removing it from the collection by value
        // equality silently no-ops if the object passed to Delete differs from the one
        // stored (e.g. a stale UI-bound copy with a different Enabled/Text value).
        var fixture = FeatureFixture.Create();
        var viewModel = await RevealOneAsync(fixture, "m-1");
        var stored = Assert.Single(fixture.LocalNotes.Notes);
        var staleCopy = stored with { Enabled = !stored.Enabled };

        await viewModel.DeleteOpenedNoteCommand.ExecuteAsync(staleCopy);

        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Empty(viewModel.OpenedNotes);
    }

    [Fact]
    public async Task Selecting_a_different_note_clears_the_pending_delete_and_the_prompt_names_the_target()
    {
        var fixture = FeatureFixture.Create(
            revealRemoteNoteAsync: (envelope, _) => Task.FromResult(new RevealedRemoteNote($"note {envelope.MessageId}", "none")));
        var viewModel = await RevealOneAsync(fixture, "m-1");
        await RevealOneAsync(fixture, "m-2", viewModel);
        Assert.Equal(2, fixture.LocalNotes.Notes.Count);
        var first = fixture.LocalNotes.Notes[0];
        var second = fixture.LocalNotes.Notes[1];

        // Revealing m-2 left it selected, so ask to delete it and then select
        // the other note: a real selection change must drop the pending delete.
        Assert.Equal(second.Id, viewModel.SelectedOpenedNote?.Id);
        viewModel.RequestDeleteOpenedNoteCommand.Execute(second);
        Assert.Equal($"delete \"{second.Text}\" for good? cannot undo", viewModel.DeleteNotePrompt);

        viewModel.SelectedOpenedNote = first;

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
        var note = new LocalLoveNote("remote-note-1", text);

        viewModel.RequestDeleteOpenedNoteCommand.Execute(note);

        Assert.Equal(
            $"delete \"{new string('a', 39)}…\" for good? cannot undo",
            viewModel.DeleteNotePrompt);
    }

    [Fact]
    public async Task Refreshing_the_love_notes_view_model_clears_a_stale_pending_delete_confirmation()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = await RevealOneAsync(fixture, "m-1");
        var note = Assert.Single(fixture.LocalNotes.Notes);
        viewModel.RequestDeleteOpenedNoteCommand.Execute(note);
        Assert.True(viewModel.IsConfirmingDeleteNote);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsConfirmingDeleteNote);
        Assert.Null(viewModel.PendingDeleteNote);
    }

    /// <summary>Adds a pending partner note and reveals it, which keeps it as an opened note.</summary>
    private static async Task<LoveNotesViewModel> RevealOneAsync(
        FeatureFixture fixture,
        string messageId,
        LoveNotesViewModel? viewModel = null)
    {
        var envelope = new RemoteEnvelope(messageId, [1], fixture.Clock.UtcNow);
        fixture.RemoteNotes.Pending.Add(envelope);
        viewModel ??= new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await viewModel.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);
        Assert.Null(viewModel.ErrorMessage);
        return viewModel;
    }

    [Fact]
    public async Task Revealing_a_remote_note_keeps_it_without_a_separate_save()
    {
        var fixture = FeatureFixture.Create();
        var envelope = new RemoteEnvelope("message-1", [1], fixture.Clock.UtcNow);
        fixture.RemoteNotes.Pending.Add(envelope);
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.LocalNotes.Notes);
        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(envelope);

        var kept = Assert.Single(fixture.LocalNotes.Notes);
        Assert.Equal("remote-message-1", kept.Id);
        Assert.Equal("You can do it", kept.Text);
        Assert.Empty(fixture.RemoteNotes.Pending);
        Assert.Equal("remote-message-1", Assert.Single(viewModel.OpenedNotes).Id);
        Assert.Contains("pet.dismiss", fixture.Events);
    }

    [Fact]
    public async Task Appearance_save_applies_the_shell_theme_only_after_persistence_succeeds()
    {
        var fixture = FeatureFixture.Create();
        var applied = new List<AppTheme>();
        var viewModel = new SettingsViewModel(fixture.Context, applied.Add) { Theme = AppTheme.Dark };

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
        var viewModel = new SettingsViewModel(fixture.Context) { PetScale = 1.5 };

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
        var viewModel = new SettingsViewModel(fixture.Context); // PetScale left at its ctor default

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
        var viewModel = new SettingsViewModel(fixture.Context) { PetScale = 1.5 };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Placements.SaveHistory);
    }

    [Fact]
    public async Task Save_placement_button_still_creates_a_new_row_for_an_unregistered_monitor()
    {
        // The explicit "save pet placement" button (as opposed to "save appearance") is the one
        // allowed to create a placement row from scratch -- that behaviour must not change.
        var fixture = FeatureFixture.Create();
        var viewModel = new SettingsViewModel(fixture.Context) { PetScale = 1.5 };

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
        var viewModel = new SettingsViewModel(fixture.Context);
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
        var viewModel = new SettingsViewModel(fixture.Context);
        await viewModel.RefreshAsync(ct);
        Assert.Equal(1.0, viewModel.PetScale);

        viewModel.MonitorDeviceName = "brand new monitor";

        Assert.Equal(1.0, viewModel.PetScale);
    }

    [Fact]
    public async Task Appearance_saves_sound_preferences_through_the_mutation_coordinator()
    {
        var fixture = FeatureFixture.Create();
        var viewModel = new SettingsViewModel(fixture.Context)
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
    public void Comfort_actions_are_only_breathe_and_stop()
    {
        // Breathe with me lives on Home; the only other choice is Stop.
        Assert.Equal(
            [ComfortAction.BreatheWithMe, ComfortAction.Close],
            Enum.GetValues<ComfortAction>());
    }

    [Fact]
    public async Task Router_fails_explicitly_when_a_settings_destination_is_not_wired()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(fixture.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.ExecuteAccessibleAsync(OverlayAction.Pet, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Router_pet_greeting_restores_the_next_durable_pet_state()
    {
        var fixture = FeatureFixture.Create();
        fixture.Context.Pet.Handle(new PetEvent.RemoteNoteArrived("next-note"));
        var router = new OverlayCommandRouter(fixture.Context);

        await router.ExecuteAsync(OverlayAction.Pet, TestContext.Current.CancellationToken);

        Assert.Equal(PetState.RemoteNote, fixture.Context.Pet.Current.State);
        Assert.Equal(
            PetState.Idle,
            fixture.Context.Pet.Handle(new PetEvent.Dismissed("next-note")).State);
    }

    [Fact]
    public async Task Router_pet_plays_petted_and_a_third_quick_pet_celebrates()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(fixture.Context);

        for (var pet = 0; pet < 3; pet++)
        {
            await router.ExecuteAsync(OverlayAction.Pet, TestContext.Current.CancellationToken);
            fixture.Clock.UtcNow += TimeSpan.FromSeconds(5);
        }

        Assert.Equal(
            ["petted", "petted", "celebrate"],
            fixture.OneShotPresentations.Select(item =>
                Assert.IsType<PetEvent.InteractionRequested>(item.Event).AnimationKey));
        Assert.Equal(["petted", "petted", "celebrate"], fixture.OneShotPresentations.Select(item => item.DismissalId));
        Assert.Equal(TimeSpan.Zero, fixture.Context.Affection.NeglectedFor);
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_pokes_that_keep_coming_make_him_grumpy_then_angry_then_cry()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(fixture.Context);

        for (var poke = 0; poke < 10; poke++)
        {
            await router.ExecuteAsync(OverlayAction.Pet, TestContext.Current.CancellationToken);
            fixture.Clock.UtcNow += TimeSpan.FromSeconds(2);
        }

        Assert.Equal(
            ["petted", "petted", "celebrate", "grumpy", "grumpy", "tantrum", "tantrum", "flail", "flail", "wail"],
            fixture.OneShotPresentations.Select(item =>
                Assert.IsType<PetEvent.InteractionRequested>(item.Event).AnimationKey));
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Context_pet_shares_the_affection_clock_with_the_overlay_router()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(fixture.Context);

        await fixture.Context.PetAsync(TestContext.Current.CancellationToken);
        await router.ExecuteAsync(OverlayAction.Pet, TestContext.Current.CancellationToken);
        await fixture.Context.PetAsync(TestContext.Current.CancellationToken);
        await fixture.Context.DrinkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["petted", "petted", "celebrate", "drink"],
            fixture.OneShotPresentations.Select(item =>
                Assert.IsType<PetEvent.InteractionRequested>(item.Event).AnimationKey));
        Assert.Equal(TimeSpan.Zero, fixture.Context.Affection.NeglectedFor);
    }

    [Fact]
    public async Task Router_drink_plays_during_a_meal_and_hands_back_to_eating()
    {
        var fixture = FeatureFixture.Create();
        fixture.Context.Pet.Handle(new PetEvent.EatingStarted("meal-1"));
        var router = new OverlayCommandRouter(fixture.Context);

        await router.ExecuteAsync(OverlayAction.DrinkWater, TestContext.Current.CancellationToken);

        var drink = Assert.Single(fixture.OneShotPresentations);
        Assert.Equal("drink", Assert.IsType<PetEvent.InteractionRequested>(drink.Event).AnimationKey);
        Assert.Equal(PetState.Eating, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_eat_together_toggles_a_meal_that_holds_notes_back()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);

        Assert.True(router.IsEating);
        Assert.Equal("done eating", router.LabelFor(OverlayAction.EatTogether));
        Assert.Equal("eat", fixture.Context.Pet.Current.AnimationKey);
        Assert.Equal(PetState.Eating, fixture.Context.Pet.Handle(new PetEvent.RemoteNoteArrived("m-1")).State);

        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);

        Assert.False(router.IsEating);
        Assert.Equal("eat together", router.LabelFor(OverlayAction.EatTogether));
        Assert.False(fixture.Context.Pet.IsEatingActive);
        Assert.Equal(PetState.RemoteNote, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_eat_together_end_that_fails_to_present_still_releases_the_meal()
    {
        var fixture = FeatureFixture.Create(beforePresent: (petEvent, _) => petEvent is PetEvent.EatingEnded
            ? Task.FromException(new InvalidOperationException("presentation failed"))
            : Task.CompletedTask);
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);
        Assert.True(fixture.Context.Pet.IsEatingActive);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken));

        Assert.False(router.IsEating);
        Assert.False(fixture.Context.Pet.IsEatingActive);
    }

    [Fact]
    public async Task Router_eat_together_toggles_are_applied_in_order()
    {
        var startPresenting = new TaskCompletionSource();
        var releaseStart = new TaskCompletionSource();
        var fixture = FeatureFixture.Create(beforePresent: async (petEvent, token) =>
        {
            if (petEvent is PetEvent.EatingStarted)
            {
                startPresenting.TrySetResult();
                await releaseStart.Task.WaitAsync(token);
            }
        });
        var pet = fixture.Context.Pet;
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        var start = router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);
        await startPresenting.Task.WaitAsync(TestContext.Current.CancellationToken);
        var end = router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);
        releaseStart.SetResult();
        await Task.WhenAll(start, end);

        Assert.False(router.IsEating);
        Assert.False(pet.IsEatingActive);
    }

    [Fact]
    public async Task Router_eat_together_ends_by_itself_after_twenty_minutes()
    {
        var fixture = FeatureFixture.Create();
        var requested = new List<TimeSpan>();
        var mealOver = new TaskCompletionSource();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) =>
            {
                requested.Add(delay);
                return mealOver.Task.WaitAsync(token);
            });

        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);
        Assert.Equal(PetState.Eating, fixture.Context.Pet.Current.State);

        mealOver.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fixture.Context.Pet.IsEatingActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal([OverlayCommandRouter.EatingDuration], requested);
        Assert.Equal(TimeSpan.FromMinutes(20), OverlayCommandRouter.EatingDuration);
        Assert.False(router.IsEating);
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_study_together_toggles_a_focus_session_that_holds_notes_back()
    {
        var fixture = FeatureFixture.Create();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await router.ExecuteAsync(OverlayAction.StudyTogether, TestContext.Current.CancellationToken);

        Assert.True(router.IsStudying);
        Assert.Equal("done studying", router.LabelFor(OverlayAction.StudyTogether));
        Assert.Equal("focus", fixture.Context.Pet.Current.AnimationKey);
        Assert.Equal(PetStateMachine.StudyingBubble, fixture.Context.Pet.Current.BubbleTitle);
        Assert.Equal(PetState.Studying, fixture.Context.Pet.Handle(new PetEvent.RemoteNoteArrived("m-1")).State);

        await router.ExecuteAsync(OverlayAction.StudyTogether, TestContext.Current.CancellationToken);

        Assert.False(router.IsStudying);
        Assert.Equal("study together", router.LabelFor(OverlayAction.StudyTogether));
        Assert.False(fixture.Context.Pet.IsStudyingActive);
        Assert.Equal(PetState.RemoteNote, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_sing_for_me_sings_for_the_song_length_and_stop_silences_it()
    {
        var stops = 0;
        var requested = new List<TimeSpan>();
        var fixture = FeatureFixture.Create(
            songDuration: TimeSpan.FromSeconds(30),
            stopSongAsync: _ =>
            {
                stops++;
                return Task.CompletedTask;
            });
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) =>
            {
                requested.Add(delay);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        await router.ExecuteAsync(OverlayAction.SingForMe, TestContext.Current.CancellationToken);

        Assert.True(router.IsSinging);
        Assert.Equal("stop singing", router.LabelFor(OverlayAction.SingForMe));
        Assert.Equal(PetState.Singing, fixture.Context.Pet.Current.State);
        Assert.Equal("sing", fixture.Context.Pet.Current.AnimationKey);
        Assert.Equal(PetStateMachine.SingingBubble, fixture.Context.Pet.Current.BubbleTitle);
        Assert.Equal([TimeSpan.FromSeconds(30)], requested);
        Assert.Equal(0, stops);

        await router.ExecuteAsync(OverlayAction.SingForMe, TestContext.Current.CancellationToken);

        Assert.False(router.IsSinging);
        Assert.Equal("sing for me", router.LabelFor(OverlayAction.SingForMe));
        Assert.False(fixture.Context.Pet.IsSingingActive);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task Router_starting_a_meal_mid_song_silences_the_song()
    {
        var stops = 0;
        var fixture = FeatureFixture.Create(stopSongAsync: _ =>
        {
            stops++;
            return Task.CompletedTask;
        });
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await router.ExecuteAsync(OverlayAction.SingForMe, TestContext.Current.CancellationToken);
        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);

        Assert.False(router.IsSinging);
        Assert.True(router.IsEating);
        Assert.Equal(1, stops);
        Assert.Equal(PetState.Eating, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Router_starting_study_ends_a_running_meal_and_vice_versa()
    {
        var fixture = FeatureFixture.Create();
        var pet = fixture.Context.Pet;
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);
        await router.ExecuteAsync(OverlayAction.StudyTogether, TestContext.Current.CancellationToken);

        Assert.False(router.IsEating);
        Assert.True(router.IsStudying);
        Assert.False(pet.IsEatingActive);
        Assert.Equal(PetState.Studying, pet.Current.State);

        await router.ExecuteAsync(OverlayAction.EatTogether, TestContext.Current.CancellationToken);

        Assert.True(router.IsEating);
        Assert.False(router.IsStudying);
        Assert.False(pet.IsStudyingActive);
        Assert.Equal(PetState.Eating, pet.Current.State);
    }

    [Fact]
    public async Task Router_study_start_that_fails_to_present_never_latches_the_pet()
    {
        var fixture = FeatureFixture.Create(beforePresent: (petEvent, _) => petEvent is PetEvent.StudyStarted
            ? Task.FromException(new InvalidOperationException("presentation failed"))
            : Task.CompletedTask);
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.ExecuteAsync(OverlayAction.StudyTogether, TestContext.Current.CancellationToken));

        Assert.False(router.IsStudying);
        Assert.False(fixture.Context.Pet.IsStudyingActive);
    }

    [Fact]
    public async Task Router_study_together_ends_by_itself_after_twenty_five_minutes()
    {
        var fixture = FeatureFixture.Create();
        var requested = new List<TimeSpan>();
        var studyOver = new TaskCompletionSource();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (delay, token) =>
            {
                requested.Add(delay);
                return studyOver.Task.WaitAsync(token);
            });

        await router.ExecuteAsync(OverlayAction.StudyTogether, TestContext.Current.CancellationToken);
        Assert.Equal(PetState.Studying, fixture.Context.Pet.Current.State);

        studyOver.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fixture.Context.Pet.IsStudyingActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal([OverlayCommandRouter.StudyDuration], requested);
        Assert.Equal(TimeSpan.FromMinutes(25), OverlayCommandRouter.StudyDuration);
        Assert.False(router.IsStudying);
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
    }

    [Fact]
    public async Task Breathing_publishes_a_finite_cycle()
    {
        var fixture = FeatureFixture.Create();
        var phases = new List<BreathVisualPhase>();
        var router = new OverlayCommandRouter(
            fixture.Context,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        router.ComfortPanelChanged += (_, _) => phases.Add(router.ComfortPanel.Phase);

        await router.ExecuteComfortAsync(ComfortAction.BreatheWithMe, TestContext.Current.CancellationToken);

        Assert.False(router.IsBreathing);
        Assert.Equal(BreathVisualPhase.Complete, router.ComfortPanel.Phase);
        Assert.Contains(BreathVisualPhase.Inhale, phases);
        Assert.Contains(BreathVisualPhase.Exhale, phases);
        // No five-minute break any more: breathing never pauses Dudu.
        Assert.Equal(PauseMode.None, fixture.Context.GetPauseState().Mode);
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
        var appearance = new SettingsViewModel(fixture.Context) { Theme = AppTheme.Dark };

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
        var appearance = new SettingsViewModel(fixture.Context)
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
    public async Task Local_deletion_reloads_clean_default_preferences()
    {
        var fixture = FeatureFixture.Create(deleteLocalDataAsync: _ => Task.CompletedTask);
        fixture.Preferences.Current = null;

        await fixture.Context.DeleteLocalDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Preferences.Default, fixture.Context.CurrentPreferences);
    }

    [Fact]
    public async Task Local_deletion_serializes_concurrent_preference_edits_and_reapplies_reloaded_state()
    {
        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloaded = Preferences.Default with { Theme = AppTheme.Dark, AmbientMinimumInterval = TimeSpan.FromMinutes(7) };
        FeatureFixture? fixture = null;
        fixture = FeatureFixture.Create(deleteLocalDataAsync: async _ =>
        {
            fixture!.Preferences.Current = reloaded;
            deleteEntered.TrySetResult();
            await releaseDelete.Task;
        });

        var delete = fixture.Context.DeleteLocalDataAsync(TestContext.Current.CancellationToken);
        await deleteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var edit = fixture.Context.UpdatePreferencesAsync(
            current => current with { ReducedMotion = true },
            TestContext.Current.CancellationToken);
        Assert.False(edit.IsCompleted);

        releaseDelete.TrySetResult();
        await delete;
        await edit;

        Assert.Equal(AppTheme.Dark, fixture.Context.CurrentPreferences.Theme);
        Assert.Equal(TimeSpan.FromMinutes(7), fixture.Context.CurrentPreferences.AmbientMinimumInterval);
        Assert.True(fixture.Context.CurrentPreferences.ReducedMotion);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.Preferences.Current);
        Assert.Equal(fixture.Context.CurrentPreferences, fixture.RuntimePreferences.Current);
    }

    [Fact]
    public async Task Remote_note_transaction_failure_does_not_mutate_view_model_state()
    {
        var fixture = FeatureFixture.Create();
        var envelope = new RemoteEnvelope("atomic-note", [1], fixture.Clock.UtcNow);
        fixture.RemoteNotes.Pending.Add(envelope);
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        fixture.Transactions.FailNextRemoteCommit = true;

        await viewModel.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Contains(envelope, viewModel.PendingRemoteNotes);
        Assert.Empty(viewModel.OpenedNotes);
        Assert.Null(viewModel.SelectedOpenedNote);
        // Not saved, so not read: the pet keeps its unread indicator.
        Assert.DoesNotContain("pet.dismiss", fixture.Events);
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

    private sealed class FeatureFixture
    {
        private FeatureFixture(
            FakeClock clock,
            FakeLocalNoteRepository localNotes,
            FakeRemoteEnvelopeRepository remoteNotes,
            FakePreferencesRepository preferences,
            FakePlacementRepository placements,
            FakeProfileRepository profiles,
            FakeFeatureTransactions transactions,
            FakeRuntimePreferences runtimePreferences,
            CompanionFeatureContext context,
            List<string> events,
            List<(PetEvent Event, string DismissalId)> oneShotPresentations)
        {
            Clock = clock;
            LocalNotes = localNotes;
            RemoteNotes = remoteNotes;
            Preferences = preferences;
            Placements = placements;
            Profiles = profiles;
            Transactions = transactions;
            RuntimePreferences = runtimePreferences;
            Context = context;
            Events = events;
            OneShotPresentations = oneShotPresentations;
        }

        public FakeClock Clock { get; }
        public FakeLocalNoteRepository LocalNotes { get; }
        public FakeRemoteEnvelopeRepository RemoteNotes { get; }
        public FakePreferencesRepository Preferences { get; }
        public FakePlacementRepository Placements { get; }
        public FakeProfileRepository Profiles { get; }
        public FakeFeatureTransactions Transactions { get; }
        public FakeRuntimePreferences RuntimePreferences { get; }
        public CompanionFeatureContext Context { get; }
        public List<string> Events { get; }
        public List<(PetEvent Event, string DismissalId)> OneShotPresentations { get; }

        public static FeatureFixture Create(
            Func<CancellationToken, Task>? deleteLocalDataAsync = null,
            Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
            Func<RemoteEnvelope, CancellationToken, Task<RevealedRemoteNote>>? revealRemoteNoteAsync = null,
            IPairingService? pairing = null,
            Func<PetEvent, CancellationToken, Task>? beforePresent = null,
            TimeSpan? songDuration = null,
            Func<CancellationToken, Task>? stopSongAsync = null)
        {
            var clock = new FakeClock("2026-09-12T10:00:00Z");
            var events = new List<string>();
            var oneShotPresentations = new List<(PetEvent Event, string DismissalId)>();
            var preferences = new Preferences(
                AppTheme.System,
                false,
                true,
                false,
                true,
                TimeSpan.FromMinutes(15));
            var preferenceRepository = new FakePreferencesRepository();
            var profileRepository = new FakeProfileRepository();
            var placementRepository = new FakePlacementRepository();
            var localNotes = new FakeLocalNoteRepository();
            var remoteNotes = new FakeRemoteEnvelopeRepository();
            var pause = PauseState.None;
            var transactions = new FakeFeatureTransactions(localNotes, remoteNotes);
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
                localNotes,
                remoteNotes,
                pairing ?? new FakePairing(),
                transactions,
                pet,
                getPauseState: () => pause,
                applyPauseAsync: (state, _) =>
                {
                    pause = state;
                    return Task.CompletedTask;
                },
                presentPetAsync: async (petEvent, token) =>
                {
                    if (beforePresent is not null)
                        await beforePresent(petEvent, token);
                    pet.Handle(petEvent);
                    events.Add(petEvent switch
                    {
                        PetEvent.Dismissed => "pet.dismiss",
                        _ => "pet.present",
                    });
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
                deleteLocalDataAsync: deleteLocalDataAsync,
                deleteRemoteDataAsync: deleteRemoteDataAsync,
                songDuration: songDuration,
                stopSongAsync: stopSongAsync);
            return new FeatureFixture(
                clock,
                localNotes,
                remoteNotes,
                preferenceRepository,
                placementRepository,
                profileRepository,
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

    private sealed class FakeLocalNoteRepository : ILocalNoteRepository
    {
        public List<LocalLoveNote> Notes { get; } = [];
        public List<LocalLoveNote> Enabled => Notes.Where(note => note.Enabled).ToList();
        public Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes);
        public Task<IReadOnlyList<LocalLoveNote>> ListRemoteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.Where(note => note.Id.StartsWith("remote-", StringComparison.Ordinal)).Reverse().ToArray());
        public Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken)
        {
            Notes.RemoveAll(item => item.Id == note.Id);
            Notes.Add(note);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string noteId, CancellationToken cancellationToken) { Notes.RemoveAll(item => item.Id == noteId); return Task.CompletedTask; }
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
        FakeLocalNoteRepository localNotes,
        FakeRemoteEnvelopeRepository remoteNotes) : ICompanionFeatureTransactions
    {
        public bool FailNextRemoteCommit { get; set; }

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
