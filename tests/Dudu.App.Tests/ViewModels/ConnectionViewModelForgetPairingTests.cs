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
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>
/// Finding 13: ConnectionViewModel.ForgetPairingAsync used to discard every
/// held remote note unconditionally, regardless of whether the forget
/// actually wiped the local remote-envelope store. RemoteSyncService.
/// ForgetPairingLocallyAsync (see src/Dudu.Infrastructure) only deletes every
/// pending envelope when the desktop's ECDH key turned out unreadable; in the
/// ordinary re-pair case the key survives and every envelope stays pending,
/// still revealable after pairing again -- discarding its held note in that
/// case silently lost an arrival that was never actually gone. These tests
/// exercise ForgetPairingAsync's new check directly against the view model,
/// with a minimal hand-built CompanionFeatureContext: the full fixture used
/// elsewhere lives in FeatureViewModelTests.cs, which is owned by another
/// workstream this round and must not be touched, so this is a new,
/// deliberately smaller file rather than an addition there.
/// </summary>
public sealed class ConnectionViewModelForgetPairingTests
{
    [Fact]
    public async Task Forget_pairing_leaves_held_remote_notes_alone_when_envelopes_still_remain()
    {
        var remoteEnvelopes = new FakeRemoteEnvelopeRepository();
        remoteEnvelopes.Pending.Add(MakeEnvelope("still-pending"));
        var discardCalls = 0;
        var context = BuildContext(
            remoteEnvelopes,
            discardHeldRemoteNotesAsync: _ =>
            {
                discardCalls++;
                return Task.CompletedTask;
            });
        var vm = new ConnectionViewModel(context);

        vm.RequestForgetPairingCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, discardCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Forget_pairing_discards_held_remote_notes_when_no_envelopes_remain()
    {
        // Simulates the unreadable-key branch: RemoteSyncService.
        // ForgetPairingLocallyAsync already wiped every envelope by the time
        // Pairing.ForgetPairingAsync returns, so the remote-envelope
        // repository is left empty -- any held note referencing one of them
        // is now orphaned and must be discarded too. FakePairing models
        // that causal order directly: it starts with a pending envelope and
        // clears the repository itself when ForgetPairingAsync runs, so the
        // later ListPendingAsync check genuinely observes the post-forget
        // state instead of an environment that was simply empty from the
        // start.
        var remoteEnvelopes = new FakeRemoteEnvelopeRepository();
        remoteEnvelopes.Pending.Add(MakeEnvelope("wiped-on-forget"));
        var discardCalls = 0;
        var context = BuildContext(
            remoteEnvelopes,
            discardHeldRemoteNotesAsync: _ =>
            {
                discardCalls++;
                return Task.CompletedTask;
            },
            wipesEnvelopesOnForget: true);
        var vm = new ConnectionViewModel(context);

        vm.RequestForgetPairingCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, discardCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Forget_pairing_still_succeeds_and_updates_state_when_the_envelope_check_throws()
    {
        // Round 4 Opus follow-up 3: Pairing.ForgetPairingAsync has already
        // committed the forget by the time the ListPendingAsync check runs.
        // A throw from that check is best-effort cleanup gone wrong, not a
        // failure of the forget itself -- it must not turn a committed
        // forget into a reported error, and must not skip the page's UI
        // state update (Availability/IsPaired going back to unpaired).
        //
        // Round 4b Opus follow-up (Finding C): every value this test used
        // to assert was already the VM's default (Offline/not paired) even
        // before ForgetPairingAsync ran, so it could pass with the
        // production code deleted. Drive the VM into a genuinely paired
        // state first via RefreshAsync, then confirm the forget actually
        // clears it.
        var remoteEnvelopes = new FakeRemoteEnvelopeRepository { ThrowOnListPending = true };
        var discardCalls = 0;
        var context = BuildContext(
            remoteEnvelopes,
            discardHeldRemoteNotesAsync: _ =>
            {
                discardCalls++;
                return Task.CompletedTask;
            },
            availabilityToReport: PairingAvailability.Available,
            sessionCountToReport: 2);
        var vm = new ConnectionViewModel(context);

        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingAvailability.Available, vm.Availability);
        Assert.Equal(2, vm.SessionCount);
        Assert.True(vm.IsPaired);

        vm.RequestForgetPairingCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(PairingAvailability.Offline, vm.Availability);
        Assert.Equal(0, vm.SessionCount);
        Assert.False(vm.IsPaired);
        // Fails closed: the check couldn't confirm envelopes are gone, so it
        // assumes they remain and leaves the (possibly stale) held note
        // alone rather than risk discarding one that is still revealable.
        Assert.Equal(0, discardCalls);
    }

    [Fact]
    public async Task Forget_pairing_trace_fallback_carries_only_the_exception_type_never_its_message()
    {
        // AGENTS.md logging rules: Trace fallbacks carry type + HResult only, never the
        // exception message or stack (a repository message can echo row data). The envelope
        // check used to trace the whole exception.
        var remoteEnvelopes = new FakeRemoteEnvelopeRepository { ThrowOnListPending = true };
        var context = BuildContext(remoteEnvelopes, discardHeldRemoteNotesAsync: _ => Task.CompletedTask);
        var vm = new ConnectionViewModel(context);
        using var writer = new StringWriter();
        var listener = new global::System.Diagnostics.TextWriterTraceListener(writer);
        global::System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            vm.RequestForgetPairingCommand.Execute(null);
            await vm.ConfirmCommand.ExecuteAsync(null);
            listener.Flush();
        }
        finally
        {
            global::System.Diagnostics.Trace.Listeners.Remove(listener);
        }

        var traced = writer.ToString();
        Assert.Contains("Dudu forget-pairing envelope check failed: IOException", traced);
        Assert.DoesNotContain("injected envelope list failure", traced);
        Assert.Null(vm.ErrorMessage);
    }

    private static RemoteEnvelope MakeEnvelope(string messageId) => new(
        messageId,
        ciphertext: [1, 2, 3],
        receivedUtc: DateTimeOffset.Parse("2026-09-19T08:00:00Z"));

    private static CompanionFeatureContext BuildContext(
        FakeRemoteEnvelopeRepository remoteEnvelopes,
        Func<CancellationToken, Task> discardHeldRemoteNotesAsync,
        bool wipesEnvelopesOnForget = false,
        PairingAvailability availabilityToReport = PairingAvailability.Offline,
        int sessionCountToReport = 0)
    {
        var clock = new FakeClock();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, true, false, true, TimeSpan.FromMinutes(15));
        var preferenceRepository = new FakePreferencesRepository();
        var preferenceMutations = new PreferenceMutationCoordinator(preferences, preferenceRepository);
        var localNotes = new FakeLocalNoteRepository();
        var tasks = new FakeTaskRepository();
        var focusSessions = new FakeFocusRepository();
        var checkIns = new FakeCheckInRepository();

        return new CompanionFeatureContext(
            clock,
            preferenceMutations,
            new FakeProfileRepository(),
            new FakePlacementRepository(),
            new FakeReminderRepository(),
            new FakeReminderRepository(),
            tasks,
            focusSessions,
            localNotes,
            remoteEnvelopes,
            new FakeCountdownRepository(),
            checkIns,
            new CheckInService(checkIns, clock),
            new TaskService(tasks, clock),
            new FocusService(focusSessions, clock, tasks),
            new LocalNoteSelector(localNotes, clock, new FixedRandom(), preferences),
            new FakePairing(remoteEnvelopes, wipesEnvelopesOnForget, availabilityToReport, sessionCountToReport),
            new ThrowingFeatureTransactions(),
            PetStateMachine.CreateIdle(),
            discardHeldRemoteNotesAsync: discardHeldRemoteNotesAsync);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedRandom : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }

    private sealed class FakeRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        public List<RemoteEnvelope> Pending { get; } = [];
        public bool ThrowOnListPending { get; init; }

        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Pending.FirstOrDefault(item => item.MessageId == messageId));

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnListPending)
            {
                throw new IOException("injected envelope list failure");
            }

            return Task.FromResult<IReadOnlyList<RemoteEnvelope>>(Pending);
        }

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
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) =>
            Task.FromResult<PetPlacement?>(null);
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PetPlacement>>([]);
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeReminderRepository : IReminderRepository, IReminderWriter
    {
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>([]);
        public Task<bool> RecordOccurrencesAndAdvanceAsync(
            Reminder reminder,
            IReadOnlyList<ReminderOccurrence> occurrences,
            DateTimeOffset? nextDueUtc,
            CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TaskItem?>(null);
        public Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskItem>>([]);
        public Task SaveAsync(TaskItem task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeFocusRepository : IFocusSessionRepository
    {
        public Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<FocusSession?>(null);
        public Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult<FocusSession?>(null);
        public Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SaveAsync(FocusSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCountdownRepository : ICountdownRepository
    {
        public Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Countdown?>(null);
        public Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Countdown>>([]);
        public Task SaveAsync(Countdown countdown, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCheckInRepository : ICheckInRepository
    {
        public Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodCheckIn>>([]);
    }

    private sealed class FakeLocalNoteRepository : ILocalNoteRepository
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

    private sealed class FakePairing(
        FakeRemoteEnvelopeRepository? remoteEnvelopes = null,
        bool wipesEnvelopesOnForget = false,
        PairingAvailability availabilityToReport = PairingAvailability.Offline,
        int sessionCountToReport = 0) : IPairingService
    {
        public int ForgetPairingCallCount { get; private set; }
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(availabilityToReport);
        // RefreshAsync tries ListSessionsAsync first and falls back to this on
        // NotSupportedException (the interface's default for both). Overriding
        // only this one keeps that fallback path exercised, same as a real
        // pairing service without per-session listing.
        public Task<int> GetSessionCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sessionCountToReport);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ForgetPairingAsync(CancellationToken cancellationToken = default)
        {
            ForgetPairingCallCount++;
            // Models RemoteSyncService.ForgetPairingLocallyAsync's
            // unreadable-key branch, which wipes every pending envelope as
            // PART OF the forget itself -- before ForgetPairingAsync
            // returns, so ConnectionViewModel's later ListPendingAsync
            // check genuinely observes the post-forget state, matching the
            // real causal order (forget commits, then the check runs).
            if (wipesEnvelopesOnForget)
            {
                remoteEnvelopes?.Pending.Clear();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFeatureTransactions : ICompanionFeatureTransactions
    {
        public Task SavePreferencesAndDefaultRemindersAsync(
            Preferences preferences, DateTimeOffset nowUtc, TimeZoneInfo localTimeZone,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for this test"));
        public Task RestorePreferencesAndDefaultRemindersAsync(
            Preferences preferences, IReadOnlyList<Reminder> previousDefaultReminders,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for this test"));
        public Task SaveRemoteNoteAndConsumeEnvelopeAsync(
            LocalLoveNote note, string messageId, DateTimeOffset processedUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("not needed for this test"));
    }
}
