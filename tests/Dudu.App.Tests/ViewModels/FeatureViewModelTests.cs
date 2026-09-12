using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
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

    private sealed class FeatureFixture
    {
        private FeatureFixture(
            FakeClock clock,
            FakeReminderRepository reminders,
            FakeLocalNoteRepository localNotes,
            FakeRemoteEnvelopeRepository remoteNotes,
            CompanionFeatureContext context,
            List<string> events)
        {
            Clock = clock;
            Reminders = reminders;
            LocalNotes = localNotes;
            RemoteNotes = remoteNotes;
            Context = context;
            Events = events;
        }

        public FakeClock Clock { get; }
        public FakeReminderRepository Reminders { get; }
        public FakeLocalNoteRepository LocalNotes { get; }
        public FakeRemoteEnvelopeRepository RemoteNotes { get; }
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
            var context = new CompanionFeatureContext(
                clock,
                preferences,
                preferenceRepository,
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
                PetStateMachine.CreateIdle(),
                getPauseState: () => pause,
                applyPauseAsync: (state, _) =>
                {
                    pause = state;
                    return Task.CompletedTask;
                },
                presentPetAsync: (petEvent, _) =>
                {
                    events.Add(petEvent is PetEvent.Dismissed ? "pet.dismiss" : "pet.present");
                    return Task.CompletedTask;
                },
                revealRemoteNoteAsync: (_, _) => Task.FromResult("You can do it"));
            return new FeatureFixture(clock, reminders, localNotes, remoteNotes, context, events);
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
        public Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) { Pending.RemoveAll(item => item.MessageId == messageId); return Task.CompletedTask; }
    }

    private sealed class FakePreferencesRepository : IPreferencesRepository
    {
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<Preferences?>(null);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken) => Task.CompletedTask;
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
    }

    private sealed class FakeFocusRepository : IFocusSessionRepository
    {
        private readonly Dictionary<Guid, FocusSession> _sessions = [];
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(_sessions.Values.FirstOrDefault(item => item.Status is FocusStatus.Running or FocusStatus.Paused));
        public Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FocusSession>>(_sessions.Values.Where(item => item.Status is not (FocusStatus.Running or FocusStatus.Paused)).ToArray());
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken) { _sessions[session.Id] = session; return Task.FromResult(true); }
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken) { _sessions[expected.Id] = replacement; return Task.FromResult(true); }
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
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodCheckIn>>([]);
    }

    private sealed class FakePairing : IPairingService
    {
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingAvailability.Offline);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
