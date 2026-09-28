using Dudu.App.Hosting;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Tasks;
using Dudu.Core.Time;

namespace Dudu.App.Tests.ViewModels;

/// <summary>
/// Minimal hand-built <see cref="CompanionFeatureContext"/> for the Settings page tests
/// (look and motion, partner connection) and the love notes reveal tests. FeatureViewModelTests
/// keeps its own (private) fixture; this one lives in its own file so those page tests can grow
/// without touching that file.
/// </summary>
internal sealed class SettingsDataPagesFixture
{
    private SettingsDataPagesFixture(
        CompanionFeatureContext context,
        MutableClock clock,
        ScriptedPairing pairing,
        MemoryPlacementRepository placements,
        MemoryPreferencesRepository preferences,
        MemoryLocalNoteRepository localNotes,
        MemoryRemoteEnvelopeRepository remoteEnvelopes,
        RecordingFeatureTransactions transactions,
        Dictionary<string, RevealedRemoteNote> revealable,
        List<string> discardedHeldRemoteNoteIds)
    {
        Context = context;
        Clock = clock;
        Pairing = pairing;
        Placements = placements;
        Preferences = preferences;
        LocalNotes = localNotes;
        RemoteEnvelopes = remoteEnvelopes;
        Transactions = transactions;
        _revealable = revealable;
        DiscardedHeldRemoteNoteIds = discardedHeldRemoteNoteIds;
    }

    private readonly Dictionary<string, RevealedRemoteNote> _revealable;

    public CompanionFeatureContext Context { get; }
    public MutableClock Clock { get; }
    public ScriptedPairing Pairing { get; }
    public MemoryPlacementRepository Placements { get; }
    public MemoryPreferencesRepository Preferences { get; }
    public MemoryLocalNoteRepository LocalNotes { get; }
    public MemoryRemoteEnvelopeRepository RemoteEnvelopes { get; }
    public RecordingFeatureTransactions Transactions { get; }

    /// <summary>Message ids whose envelope was consumed by a save-and-consume transaction.</summary>
    public List<string> ConsumedMessageIds => Transactions.ConsumedMessageIds;

    /// <summary>Message ids the view model asked the presentation gateway to drop a held copy of.</summary>
    public List<string> DiscardedHeldRemoteNoteIds { get; }

    /// <summary>Adds one pending (unopened) encrypted note; the default reveal delegate
    /// "decrypts" it to <paramref name="plaintext"/> with <paramref name="reaction"/>.</summary>
    public RemoteEnvelope AddPendingEnvelope(string messageId, string plaintext, string reaction = "none")
    {
        var envelope = new RemoteEnvelope(messageId, [1, 2, 3], Clock.UtcNow);
        RemoteEnvelopes.Pending.Add(envelope);
        _revealable[messageId] = new RevealedRemoteNote(plaintext, reaction);
        return envelope;
    }

    public LoveNotesViewModel CreateLoveNotesViewModel() => new(Context);

    public static SettingsDataPagesFixture Create(
        TimeZoneInfo? localTimeZone = null,
        Preferences? initialPreferences = null,
        Func<CancellationToken, Task>? backupAsync = null,
        Func<CancellationToken, Task>? restoreAsync = null,
        Func<CancellationToken, Task>? deleteLocalDataAsync = null,
        Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
        Func<string, CancellationToken, Task>? setGlobalShortcutAsync = null,
        IFocusSessionRepository? focusSessions = null,
        Func<PetEvent, CancellationToken, Task>? presentPetAsync = null,
        Func<string?>? getGlobalShortcutStatus = null,
        Func<CancellationToken, Task>? stopRemoteSyncAsync = null,
        Func<CancellationToken, Task>? startRemoteSyncAsync = null,
        bool remoteDeleteAvailable = false,
        Func<RemoteEnvelope, CancellationToken, Task<RevealedRemoteNote>>? revealRemoteNoteAsync = null)
    {
        var clock = new MutableClock(
            DateTimeOffset.Parse("2026-09-19T08:00:00Z"),
            localTimeZone ?? TimeZoneInfo.Utc);
        var preferences = initialPreferences ?? Dudu.Core.Models.Preferences.Default;
        var preferenceRepository = new MemoryPreferencesRepository();
        var preferenceMutations = new PreferenceMutationCoordinator(preferences, preferenceRepository);
        var localNotes = new MemoryLocalNoteRepository();
        var remoteEnvelopes = new MemoryRemoteEnvelopeRepository();
        var transactions = new RecordingFeatureTransactions(localNotes, remoteEnvelopes);
        var revealable = new Dictionary<string, RevealedRemoteNote>(StringComparer.Ordinal);
        var discardedHeldRemoteNoteIds = new List<string>();
        var tasks = new EmptyTaskRepository();
        var focusSessionRepository = focusSessions ?? new EmptyFocusRepository();
        var checkIns = new EmptyCheckInRepository();
        var placements = new MemoryPlacementRepository();
        var pairing = new ScriptedPairing();
        var context = new CompanionFeatureContext(
            clock,
            preferenceMutations,
            new EmptyProfileRepository(),
            placements,
            tasks,
            focusSessionRepository,
            localNotes,
            remoteEnvelopes,
            new EmptyCountdownRepository(),
            checkIns,
            new CheckInService(checkIns, clock),
            new TaskService(tasks, clock),
            new FocusService(focusSessionRepository, clock, tasks),
            new LocalNoteSelector(localNotes, clock, new FixedRandom(), preferences),
            pairing,
            transactions,
            PetStateMachine.CreateIdle(),
            backupAsync: backupAsync,
            restoreAsync: restoreAsync,
            deleteLocalDataAsync: deleteLocalDataAsync,
            deleteRemoteDataAsync: deleteRemoteDataAsync,
            setGlobalShortcutAsync: setGlobalShortcutAsync,
            presentPetAsync: presentPetAsync,
            getGlobalShortcutStatus: getGlobalShortcutStatus,
            stopRemoteSyncAsync: stopRemoteSyncAsync,
            startRemoteSyncAsync: startRemoteSyncAsync,
            remoteDeleteAvailable: remoteDeleteAvailable,
            revealRemoteNoteAsync: revealRemoteNoteAsync ?? ((envelope, _) =>
                revealable.TryGetValue(envelope.MessageId, out var revealed)
                    ? Task.FromResult(revealed)
                    : Task.FromException<RevealedRemoteNote>(new KeyNotFoundException("aiyo cant find that note anymore"))),
            discardHeldRemoteNoteAsync: (messageId, _) =>
            {
                discardedHeldRemoteNoteIds.Add(messageId);
                return Task.CompletedTask;
            });
        return new SettingsDataPagesFixture(
            context,
            clock,
            pairing,
            placements,
            preferenceRepository,
            localNotes,
            remoteEnvelopes,
            transactions,
            revealable,
            discardedHeldRemoteNoteIds);
    }

    /// <summary>A fixed-offset zone five hours west of UTC, built in code so the tests do not
    /// depend on the host's time-zone database.</summary>
    public static TimeZoneInfo UtcMinusFive { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "dudu-test-utc-minus-5",
        TimeSpan.FromHours(-5),
        "UTC-05 test",
        "UTC-05 test");

    internal sealed class MutableClock(DateTimeOffset utcNow, TimeZoneInfo zone) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public TimeZoneInfo LocalTimeZone => zone;
    }

    /// <summary>Pairing fake whose answers each test scripts directly.</summary>
    internal sealed class ScriptedPairing : IPairingService
    {
        public PairingAvailability State { get; set; } = PairingAvailability.Available;
        public PairingStatusReason Reason { get; set; } = PairingStatusReason.None;
        public int SessionCount { get; set; }
        public Func<PairingCodeResult>? NextCode { get; set; }
        public Exception? CreateCodeException { get; set; }
        public TaskCompletionSource? RevokeGate { get; set; }
        public int RevokeCallCount { get; private set; }
        public PairingOperationResult RevokeResult { get; set; } = PairingOperationResult.Success;

        public PairingStatusReason StatusReason => Reason;

        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task<int> GetSessionCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionCount);

        public Task<IReadOnlyList<PairingSessionSummary>> ListSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PairingSessionSummary>>([]);

        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default)
        {
            if (CreateCodeException is { } exception) return Task.FromException<PairingCodeResult>(exception);
            return Task.FromResult(NextCode?.Invoke() ?? PairingCodeResult.Offline);
        }

        public async Task<PairingOperationResult> RevokeSessionsWithResultAsync(
            CancellationToken cancellationToken = default)
        {
            RevokeCallCount++;
            if (RevokeGate is { } gate) await gate.Task;
            return RevokeResult;
        }

        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ForgetPairingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class MemoryPreferencesRepository : IPreferencesRepository
    {
        public Preferences? Current { get; private set; }
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            Current = preferences;
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryPlacementRepository : IPetPlacementRepository
    {
        public List<PetPlacement> Rows { get; } = [];
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.FirstOrDefault(row => row.MonitorDeviceName == monitorDeviceName));
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PetPlacement>>(Rows.ToArray());
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(row => row.MonitorDeviceName == placement.MonitorDeviceName);
            Rows.Add(placement);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(row => row.MonitorDeviceName == monitorDeviceName);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedRandom : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    /// <summary>In-memory pending envelopes. Lists return fresh instances (new byte[]
    /// copies) like the SQLite repository, so record equality never matches across reloads.</summary>
    internal sealed class MemoryRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public List<RemoteEnvelope> Pending { get; } = [];
        private static RemoteEnvelope Fresh(RemoteEnvelope envelope) =>
            envelope with { Ciphertext = envelope.Ciphertext.ToArray() };
        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Pending.Where(item => item.MessageId == messageId).Select(Fresh).FirstOrDefault());
        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEnvelope>>(Pending.Select(Fresh).ToArray());
        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken)
        {
            Pending.Add(envelope);
            return Task.FromResult(true);
        }
        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task<bool> TryMarkProcessedAsync(
            string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task<bool> TryConsumeAsync(
            string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Pending.RemoveAll(item => item.MessageId == messageId) == 1);
        public Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task DeleteAsync(string messageId, CancellationToken cancellationToken)
        {
            Pending.RemoveAll(item => item.MessageId == messageId);
            return Task.CompletedTask;
        }
        public Task<int> PruneExpiredAsync(
            DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken)
        {
            var count = Pending.Count;
            Pending.Clear();
            return Task.FromResult(count);
        }
    }

    private sealed class EmptyProfileRepository : IProfileRepository
    {
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<Profile?>(null);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyTaskRepository : ITaskRepository
    {
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TaskItem?>(null);
        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskItem>>([]);
        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyFocusRepository : IFocusSessionRepository
    {
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<FocusSession?>(null);
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult<FocusSession?>(null);
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyCountdownRepository : ICountdownRepository
    {
        public Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Countdown?>(null);
        public Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Countdown>>([]);
        public Task SaveAsync(Countdown countdown, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyCheckInRepository : ICheckInRepository
    {
        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodCheckIn>>([]);
    }

    /// <summary>In-memory <c>local_notes</c>: rows in insertion order, an upsert keeps a
    /// row's place (like the SQLite <c>ON CONFLICT DO UPDATE</c>), and
    /// <see cref="ListRemoteAsync"/> returns only <c>remote-</c> rows, newest first.</summary>
    internal sealed class MemoryLocalNoteRepository : ILocalNoteRepository
    {
        public List<LocalLoveNote> Notes { get; } = [];
        public Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.ToArray());
        public Task<IReadOnlyList<LocalLoveNote>> ListRemoteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes
                .Where(note => note.Id.StartsWith("remote-", StringComparison.Ordinal))
                .Reverse()
                .ToArray());
        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>(Notes.Where(note => note.Enabled).ToArray());
        public Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken)
        {
            var index = Notes.FindIndex(item => item.Id == note.Id);
            if (index >= 0) Notes[index] = note;
            else Notes.Add(note);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string noteId, CancellationToken cancellationToken)
        {
            Notes.RemoveAll(item => item.Id == noteId);
            return Task.CompletedTask;
        }
        public Task<int> CountUnsolicitedShownAsync(DateOnly localDate, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> TryRecordShownAsync(
            string noteId, DateTimeOffset shownUtc, DateOnly localDate, int dailyLimit, bool unsolicited,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>Records save-and-consume calls and applies them to the in-memory note and
    /// envelope stores as one unit: nothing changes when <see cref="FailNextRemoteCommit"/>
    /// is set.</summary>
    internal sealed class RecordingFeatureTransactions(
        MemoryLocalNoteRepository localNotes,
        MemoryRemoteEnvelopeRepository remoteEnvelopes) : ICompanionFeatureTransactions
    {
        public List<string> ConsumedMessageIds { get; } = [];
        public bool FailNextRemoteCommit { get; set; }

        public async Task SaveRemoteNoteAndConsumeEnvelopeAsync(
            LocalLoveNote note, string messageId, DateTimeOffset processedUtc,
            CancellationToken cancellationToken = default)
        {
            if (FailNextRemoteCommit)
            {
                FailNextRemoteCommit = false;
                throw new IOException("injected remote transaction failure");
            }

            if (!remoteEnvelopes.Pending.Any(item => item.MessageId == messageId))
            {
                throw new InvalidOperationException("Remote note is unavailable.");
            }

            await localNotes.SaveToJarAsync(note, cancellationToken);
            await remoteEnvelopes.TryConsumeAsync(messageId, processedUtc, cancellationToken);
            ConsumedMessageIds.Add(messageId);
        }
    }
}
