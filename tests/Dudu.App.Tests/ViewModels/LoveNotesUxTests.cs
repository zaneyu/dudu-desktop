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

/// <summary>UI/UX regressions for the Love Notes page. Uses its own fixture so it
/// can model the production stores' compare-and-set semantics and freshly
/// reloaded rows.</summary>
public sealed class LoveNotesUxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --------------------------------------------------------------- love notes

    [Fact]
    public async Task Revealing_after_a_refresh_removes_the_note_from_the_incoming_list()
    {
        // RemoteEnvelope carries byte[] fields, so a reloaded row never equals
        // the instance that was listed before; Remove(envelope) silently failed and
        // the opened note stayed listed as unopened.
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        var staleRow = viewModel.PendingRemoteNotes.Single();
        await viewModel.RefreshAsync(Ct);

        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(staleRow);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Empty(viewModel.PendingRemoteNotes);
        Assert.Equal(0, viewModel.UnopenedRemoteNoteCount);
        Assert.Equal("remote-m-1", Assert.Single(viewModel.OpenedNotes).Id);
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
    public async Task Refresh_drops_an_opened_note_that_was_deleted_elsewhere()
    {
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(viewModel.PendingRemoteNotes.Single());
        Assert.Equal("you can do it", viewModel.SelectedOpenedNoteText);

        fixture.LocalNotes.Notes.Clear(); // e.g. delete my data meanwhile
        await viewModel.RefreshAsync(Ct);

        Assert.Empty(viewModel.OpenedNotes);
        Assert.Null(viewModel.SelectedOpenedNote);
        Assert.Null(viewModel.SelectedOpenedNoteText);
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
    public async Task Opened_notes_empty_state_follows_the_list()
    {
        var fixture = Fixture.Create();
        fixture.RemoteNotes.Pending.Add(new RemoteEnvelope("m-1", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T09:00:00Z")));
        var viewModel = new LoveNotesViewModel(fixture.Context);
        await viewModel.RefreshAsync(Ct);
        Assert.True(viewModel.HasNoOpenedNotes);

        await viewModel.RevealRemoteNoteCommand.ExecuteAsync(viewModel.PendingRemoteNotes.Single());

        Assert.False(viewModel.HasNoOpenedNotes);
    }

    // ------------------------------------------------------------------ helpers

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
        public Task<IReadOnlyList<LocalLoveNote>> ListRemoteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.Where(note => note.Id.StartsWith("remote-", StringComparison.Ordinal)).Reverse().ToArray());
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
