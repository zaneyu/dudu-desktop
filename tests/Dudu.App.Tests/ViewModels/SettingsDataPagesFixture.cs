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
/// Minimal hand-built <see cref="CompanionFeatureContext"/> for the Connection, Privacy &amp;
/// Data and Appearance page tests. FeatureViewModelTests keeps its own (private) fixture; this
/// one lives in its own file so those page tests can grow without touching that file.
/// </summary>
internal sealed class SettingsDataPagesFixture
{
    private SettingsDataPagesFixture(
        CompanionFeatureContext context,
        MutableClock clock,
        ScriptedPairing pairing,
        MemoryPlacementRepository placements,
        MemoryPreferencesRepository preferences)
    {
        Context = context;
        Clock = clock;
        Pairing = pairing;
        Placements = placements;
        Preferences = preferences;
    }

    public CompanionFeatureContext Context { get; }
    public MutableClock Clock { get; }
    public ScriptedPairing Pairing { get; }
    public MemoryPlacementRepository Placements { get; }
    public MemoryPreferencesRepository Preferences { get; }

    public static SettingsDataPagesFixture Create(
        TimeZoneInfo? localTimeZone = null,
        Preferences? initialPreferences = null,
        Func<CancellationToken, Task>? backupAsync = null,
        Func<CancellationToken, Task>? restoreAsync = null,
        Func<CancellationToken, Task>? deleteLocalDataAsync = null,
        Func<CancellationToken, Task>? deleteRemoteDataAsync = null,
        Func<string, CancellationToken, Task>? setGlobalShortcutAsync = null,
        IFocusSessionRepository? focusSessions = null)
    {
        var clock = new MutableClock(
            DateTimeOffset.Parse("2026-09-19T08:00:00Z"),
            localTimeZone ?? TimeZoneInfo.Utc);
        var preferences = initialPreferences ?? Dudu.Core.Models.Preferences.Default;
        var preferenceRepository = new MemoryPreferencesRepository();
        var preferenceMutations = new PreferenceMutationCoordinator(preferences, preferenceRepository);
        var localNotes = new EmptyLocalNoteRepository();
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
            new EmptyReminderRepository(),
            new EmptyReminderRepository(),
            tasks,
            focusSessionRepository,
            localNotes,
            new EmptyRemoteEnvelopeRepository(),
            new EmptyCountdownRepository(),
            checkIns,
            new CheckInService(checkIns, clock),
            new TaskService(tasks, clock),
            new FocusService(focusSessionRepository, clock, tasks),
            new LocalNoteSelector(localNotes, clock, new FixedRandom(), preferences),
            pairing,
            new UnusedFeatureTransactions(),
            PetStateMachine.CreateIdle(),
            backupAsync: backupAsync,
            restoreAsync: restoreAsync,
            deleteLocalDataAsync: deleteLocalDataAsync,
            deleteRemoteDataAsync: deleteRemoteDataAsync,
            setGlobalShortcutAsync: setGlobalShortcutAsync);
        return new SettingsDataPagesFixture(context, clock, pairing, placements, preferenceRepository);
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

    private sealed class EmptyRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEnvelope?>(null);
        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEnvelope>>([]);
        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task<bool> TryMarkProcessedAsync(
            string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> PruneExpiredAsync(
            DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class EmptyProfileRepository : IProfileRepository
    {
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<Profile?>(null);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyReminderRepository : IReminderRepository, IReminderWriter
    {
        public Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);
        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class EmptyLocalNoteRepository : ILocalNoteRepository
    {
        public Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalLoveNote>>([]);
        public Task<int> CountUnsolicitedShownAsync(DateOnly localDate, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> TryRecordShownAsync(
            string noteId, DateTimeOffset shownUtc, DateOnly localDate, int dailyLimit, bool unsolicited,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class UnusedFeatureTransactions : ICompanionFeatureTransactions
    {
        public Task SavePreferencesAndDefaultRemindersAsync(
            Preferences preferences, DateTimeOffset nowUtc, TimeZoneInfo localTimeZone,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for these tests"));
        public Task RestorePreferencesAndDefaultRemindersAsync(
            Preferences preferences, IReadOnlyList<Reminder> previousDefaultReminders,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for these tests"));
        public Task SaveRemoteNoteAndConsumeEnvelopeAsync(
            LocalLoveNote note, string messageId, DateTimeOffset processedUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for these tests"));
    }
}
