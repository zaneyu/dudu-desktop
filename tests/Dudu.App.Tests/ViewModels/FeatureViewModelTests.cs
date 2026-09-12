using Dudu.App.Overlay;
using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class FeatureViewModelTests
{
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

        Assert.Contains("pet.focus-end", fixture.Events);
        Assert.Equal(PetState.Idle, fixture.Context.Pet.Current.State);
        Assert.Equal(FocusStatus.EndedEarly, viewModel.ActiveFocus!.Status);
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
            FakeTaskRepository tasks,
            FakeFocusRepository focusSessions,
            FakeCountdownRepository countdowns,
            FakeFeatureTransactions transactions,
            FakeRuntimePreferences runtimePreferences,
            CompanionFeatureContext context,
            List<string> events)
        {
            Clock = clock;
            Reminders = reminders;
            LocalNotes = localNotes;
            RemoteNotes = remoteNotes;
            Preferences = preferences;
            Tasks = tasks;
            FocusSessions = focusSessions;
            Countdowns = countdowns;
            Transactions = transactions;
            RuntimePreferences = runtimePreferences;
            Context = context;
            Events = events;
        }

        public FakeClock Clock { get; }
        public FakeReminderRepository Reminders { get; }
        public FakeLocalNoteRepository LocalNotes { get; }
        public FakeRemoteEnvelopeRepository RemoteNotes { get; }
        public FakePreferencesRepository Preferences { get; }
        public FakeTaskRepository Tasks { get; }
        public FakeFocusRepository FocusSessions { get; }
        public FakeCountdownRepository Countdowns { get; }
        public FakeFeatureTransactions Transactions { get; }
        public FakeRuntimePreferences RuntimePreferences { get; }
        public CompanionFeatureContext Context { get; }
        public List<string> Events { get; }
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

        public static FeatureFixture Create()
        {
            var clock = new FakeClock("2026-09-12T10:00:00Z");
            var events = new List<string>();
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
            var focusService = new FocusService(focusSessions, tasks, clock);
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
                new FakePairing(),
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
                revealRemoteNoteAsync: (_, _) => Task.FromResult("You can do it"));
            return new FeatureFixture(
                clock,
                reminders,
                localNotes,
                remoteNotes,
                preferenceRepository,
                tasks,
                focusSessions,
                countdowns,
                transactions,
                runtimePreferences,
                context,
                events);
        }
    }

    private sealed class FakeClock(string value) : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse(value);
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
        public Task RecordOccurrencesAndAdvanceAsync(Reminder reminder, IReadOnlyList<ReminderOccurrence> occurrences, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken)
        {
            events.Add("repository.complete");
            return Task.CompletedTask;
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
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Preferences? Current { get; private set; }
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
        public IReadOnlyCollection<TaskItem> Items => _tasks.Values;
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_tasks.GetValueOrDefault(id));
        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.Values.Where(item => !item.IsCompleted).ToArray());
        public Task<IReadOnlyList<TaskItem>> ListCompletedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskItem>>(_tasks.Values.Where(item => item.IsCompleted).ToArray());
        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken) { _tasks[task.Id] = task; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) { _tasks.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeFocusRepository : IFocusSessionRepository
    {
        private readonly Dictionary<Guid, FocusSession> _sessions = [];
        public FocusSession? Active => _sessions.Values.FirstOrDefault(
            item => item.Status is FocusStatus.Running or FocusStatus.Paused);
        public bool RejectCreate { get; set; }
        public bool ThrowOnCreate { get; set; }
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);
        public Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FocusSession>>(_sessions.Values.Where(item => item.Status is not (FocusStatus.Running or FocusStatus.Paused)).ToArray());
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
        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodCheckIn>>([]);
    }

    private sealed class FakePairing : IPairingService
    {
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingAvailability.Offline);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
            foreach (var reminder in Dudu.Core.Reminders.LocalReminderDefaults.Create(
                value,
                nowUtc,
                localTimeZone))
            {
                await reminders.SaveAsync(reminder, cancellationToken);
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
