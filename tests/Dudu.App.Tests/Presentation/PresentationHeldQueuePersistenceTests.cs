using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Presentation;

/// <summary>
/// P2-B: an item held by quiet hours/fullscreen/lock/pause used to live only
/// in <see cref="PresentationPolicy"/>'s in-memory queue, so quitting or
/// crashing while it was held lost it silently. <see cref="PresentationCoordinator"/>
/// now mirrors that queue into an <see cref="IHeldPresentationRepository"/> --
/// these tests exercise that mirroring against a fake repository, not a real
/// database (see <c>HeldPresentationRepositoryTests</c> in
/// Dudu.Infrastructure.Tests for the SQLite round trip).
/// </summary>
public sealed class PresentationHeldQueuePersistenceTests
{
    [Fact]
    public async Task A_suppressed_item_is_persisted_and_its_row_removed_once_released()
    {
        var repository = new RecordingHeldPresentationRepository();
        var pet = PetStateMachine.CreateIdle();
        var quiet = true;
        var played = 0;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            pet,
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch", body: "text", animationKey: "wave"),
            bypassSuppression: false,
            CancellationToken.None);

        var saved = Assert.Single(repository.Rows.Values);
        Assert.Equal("Reminder:reminder-1", saved.Key);
        Assert.Equal("Reminder", saved.Kind);
        Assert.Equal("reminder-1", saved.Id);
        Assert.Equal("Stretch", saved.Title);
        Assert.Equal("text", saved.Body);
        Assert.Equal("wave", saved.AnimationKey);
        Assert.Equal(0, played);

        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task RemoteNote_items_persist_only_their_message_id_never_title_or_body()
    {
        // Privacy: DurableNotification.RemoteNote carries nothing beyond the
        // message id (see docs/privacy.md), so the persisted row must not
        // either -- it already exists as ciphertext in remote_envelopes.
        var repository = new RecordingHeldPresentationRepository();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => true,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        var messageId = Guid.NewGuid().ToString("D");

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(messageId),
            bypassSuppression: false,
            CancellationToken.None);

        var saved = Assert.Single(repository.Rows.Values);
        Assert.Equal(messageId, saved.Id);
        Assert.Null(saved.Title);
        Assert.Null(saved.Body);
        Assert.Null(saved.AnimationKey);
    }

    [Fact]
    public async Task An_item_purged_as_expired_while_held_has_its_row_removed()
    {
        var repository = new RecordingHeldPresentationRepository();
        var now = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => true,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => now,
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch", expiresUtc: now.AddMinutes(1)),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Single(repository.Rows);

        now = now.AddMinutes(2);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_failed_release_re_persists_the_item_for_a_later_retry()
    {
        var repository = new RecordingHeldPresentationRepository();
        var attempt = 0;
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) =>
            {
                attempt++;
                return attempt == 1
                    ? Task.FromException(new InvalidOperationException("playback failed"))
                    : Task.CompletedTask;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Single(repository.Rows);

        // Released, but playback fails: the row must still exist afterward,
        // not be lost, so a restart before the next successful retry does
        // not silently drop the reminder.
        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Single(repository.Rows);

        // Retry succeeds: the row is finally cleared.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task StartAsync_reloads_a_persisted_held_item_into_the_queue()
    {
        // Simulates a restart: the repository already has a row from a
        // previous process (nothing published it this run).
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(new HeldPresentation(
            "Reminder:reminder-1",
            "Reminder",
            "reminder-1",
            "Stretch",
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-09-19T08:00:00Z"),
            Toasted: false));
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var played = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => DateTimeOffset.Parse("2026-09-19T08:05:00Z"),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);

        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, played);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task StartAsync_reloads_a_persisted_local_note_held_item_into_the_queue()
    {
        // Same restart scenario as the reminder case above, but for a
        // LocalNote row -- ToDurableNotification's LocalNote branch requires
        // both Body and AnimationKey, unlike RemoteNote's id-only row.
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(new HeldPresentation(
            "LocalNote:note-1",
            "LocalNote",
            "note-1",
            "A little note for you",
            "You are doing great.",
            "peek",
            null,
            DateTimeOffset.Parse("2026-09-19T08:00:00Z"),
            Toasted: false));
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var played = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => DateTimeOffset.Parse("2026-09-19T08:05:00Z"),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);
        Assert.Equal(1, policy.QueuedCount);

        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, played);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task StartAsync_drops_an_expired_persisted_row_without_queuing_it()
    {
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(new HeldPresentation(
            "Reminder:bedtime",
            "Reminder",
            "bedtime",
            "Goodnight",
            null,
            null,
            DateTimeOffset.Parse("2026-09-19T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-18T20:00:00Z"),
            Toasted: false));
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            // Past the row's ExpiresUtc.
            utcNow: () => DateTimeOffset.Parse("2026-09-19T09:00:00Z"),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task StartAsync_drops_and_deletes_a_row_with_an_unrecognized_kind()
    {
        // A row this build's PresentationItemKind enum no longer has a case
        // for (e.g. left over from a newer or since-removed kind) must not
        // wedge startup -- ToDurableNotification's `_ => throw` fallback is
        // caught in LoadHeldItemsAsync and the corrupt row is dropped and
        // deleted rather than queued or retried forever.
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(new HeldPresentation(
            "Bogus:ghost",
            "Bogus",
            "ghost",
            null,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-09-19T08:00:00Z"),
            Toasted: false));
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_toast_shown_while_held_is_not_shown_again_after_a_restart_reload()
    {
        // H2: _toastedWhileHeldIds used to live only in memory, so a
        // reminder toasted once while Dudu was hidden in the tray would
        // toast a second time once a restart reloaded the same row from
        // disk. The persisted `toasted` column must survive that restart
        // and suppress the duplicate.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var firstRun = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        // The only suppression reason is Dudu being hidden from the tray,
        // which is exactly the case that toasts immediately while holding
        // the animation back (see PublishAsync's toastNow).
        firstRun.SetUserVisible(false);

        await firstRun.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        var saved = Assert.Single(repository.Rows.Values);
        Assert.True(saved.Toasted);

        // Simulate a restart: a fresh coordinator instance sharing only the
        // repository's rows, nothing carried over in memory.
        var secondRun = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        await secondRun.StartAsync(CancellationToken.None);
        secondRun.SetUserVisible(true);

        await secondRun.TickAsync(CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_repository_that_throws_on_every_call_never_breaks_in_memory_presentation()
    {
        // L8: persistence is a secondary concern layered on top of
        // PresentationPolicy's in-memory queue (see PersistHeldAsync's and
        // RemoveHeldAsync's doc comments) -- a repository that fails on
        // every single call must never stop StartAsync/PublishAsync/
        // TickAsync from working, only fail to survive a restart.
        var repository = new ThrowingHeldPresentationRepository();
        var played = 0;
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
    }

    [Fact]
    public async Task A_persist_that_races_a_concurrent_presenter_does_not_delete_the_row_before_it_finishes()
    {
        // Finding 12: PersistHeldAsync's post-save guard used to delete the
        // row whenever the item was no longer sitting in PresentationPolicy's
        // queue -- but a concurrent TickAsync dequeues an item into
        // _presentingIds (not the queue) while it presents, which is exactly
        // as "not queued" as gone for good. If that concurrent presentation
        // then fails, TickAsync's decline path requeues from memory without
        // re-persisting (by design, to preserve QueuedUtc), relying on the
        // row PersistHeldAsync just deleted. The guard must also check
        // _presentingIds before deleting.
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new BlockingSaveHeldPresentationRepository(saveStarted, releaseSave.Task);
        var playbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlayback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiet = true;
        var playbackAttempt = 0;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                playbackAttempt++;
                if (playbackAttempt == 1)
                {
                    playbackStarted.TrySetResult();
                    await releasePlayback.Task;
                    throw new InvalidOperationException("playback failed");
                }
            },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        var item = DurableNotification.Reminder("reminder-1", "Stretch");

        // PublishAsync enqueues item and starts persisting it; block mid-save.
        var publish = coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        await saveStarted.Task;

        // A concurrent tick dequeues the same item for presentation while the
        // save above is still in flight.
        quiet = false;
        var tick = coordinator.TickAsync(CancellationToken.None);
        await playbackStarted.Task;

        // Let the save finish: the item is no longer in the queue (the tick
        // dequeued it), but it IS in _presentingIds, so the row must survive.
        releaseSave.SetResult();
        await publish;

        var saved = Assert.Single(repository.Rows.Values);
        Assert.Equal(item.Key, saved.Key);

        // The presentation then fails and TickAsync requeues without
        // re-persisting -- the row from above is what makes that safe.
        releasePlayback.SetResult();
        await tick;
        Assert.Single(repository.Rows);

        // A later, successful tick clears it.
        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task DiscardHeldAsync_removes_a_queued_items_marker_row_and_queue_entry()
    {
        // Finding 13: completing a reminder from the Reminders page advances
        // it directly, never going through PresentAsync -- so a copy that
        // was separately queued/held (e.g. it became due while she had Dudu
        // hidden) must be discarded explicitly, or it resurfaces on a later
        // tick or the next launch even though it was already handled.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var played = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        // Held for the sole reason of being hidden from the tray -- the one
        // case where the toast fires immediately while still held (see
        // PublishAsync's toastNow). Verify DiscardHeldAsync clears the
        // toasted-while-held marker too, not just the row/queue entry.
        coordinator.SetUserVisible(false);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Single(repository.Rows);
        Assert.Equal(1, policy.QueuedCount);

        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "reminder-1", CancellationToken.None);

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);

        // If it were still queued, un-hiding and ticking would present it.
        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, played);
        Assert.Equal(1, notifications.ReminderCalls);
    }

    [Fact]
    public void Held_presentation_key_format_matches_the_literal_strings_the_cascading_deletes_rely_on()
    {
        // M3's cascading deletes in LocalNoteRepository/ReminderRepository/
        // RemoteEnvelopeRepository match a held row's key by literal string
        // ('LocalNote:' || $id, etc.) since Infrastructure cannot reference
        // this App-layer enum. This is the tripwire: if PresentationItemKind
        // were ever renamed, those SQL literals would silently stop matching
        // and M3's fix would quietly regress with no compile error.
        Assert.Equal("RemoteNote:msg-1", DurableNotification.RemoteNote("msg-1").Key);
        Assert.Equal("Reminder:reminder-1", DurableNotification.Reminder("reminder-1", "Stretch").Key);
        Assert.Equal(
            "LocalNote:note-1",
            DurableNotification.LocalNote(new LocalLoveNote("note-1", "Hi"), "wave").Key);
    }

    [Fact]
    public async Task ReportHeldFailureOnce_reports_a_second_failure_of_a_different_exception_type_under_the_same_kind()
    {
        // Finding 16: the report-once latch used to key on operation kind
        // alone ("persist"), so one already-reported transient failure (e.g.
        // a busy database) would permanently silence a later, genuinely
        // different failure under the same kind (e.g. a corrupt database) --
        // exactly the case someone would need diagnosed. Keying on kind plus
        // exception type (and Sqlite error code) fixes that.
        var reporter = new RecordingErrorReporter();
        var repository = new AlternatingThrowHeldPresentationRepository();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => true,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            errorReporter: reporter,
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-2", "Drink water"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(2, reporter.Reports.Count);
        Assert.Contains(reporter.Reports, r => r.Exception is InvalidOperationException);
        Assert.Contains(reporter.Reports, r => r.Exception is NotSupportedException);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CountingNotificationService : INotificationService
    {
        public int ReminderCalls { get; private set; }

        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
        {
            ReminderCalls++;
            return Task.CompletedTask;
        }

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingHeldPresentationRepository : IHeldPresentationRepository
    {
        public Dictionary<string, HeldPresentation> Rows { get; } = [];

        public void Seed(HeldPresentation item) => Rows[item.Key] = item;

        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeldPresentation>>(Rows.Values.ToArray());

        public Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
        {
            Rows[item.Key] = item;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Rows.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHeldPresentationRepository : IHeldPresentationRepository
    {
        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated repository failure");

        public Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated repository failure");

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated repository failure");
    }

    /// <summary>A fake whose <see cref="SaveAsync"/> signals <paramref
    /// name="started"/> then blocks on <paramref name="release"/> before
    /// actually storing the row, so a test can control the exact window
    /// during which a save is in flight.</summary>
    private sealed class BlockingSaveHeldPresentationRepository(
        TaskCompletionSource started,
        Task release) : IHeldPresentationRepository
    {
        public Dictionary<string, HeldPresentation> Rows { get; } = [];

        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeldPresentation>>(Rows.Values.ToArray());

        public async Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release;
            Rows[item.Key] = item;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Rows.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>A fake whose <see cref="SaveAsync"/> throws a different
    /// exception type on each successive call, for testing that the
    /// report-once latch is keyed on more than just the operation kind.</summary>
    private sealed class AlternatingThrowHeldPresentationRepository : IHeldPresentationRepository
    {
        private int _calls;

        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeldPresentation>>(Array.Empty<HeldPresentation>());

        public Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
        {
            _calls++;
            return Task.FromException(_calls == 1
                ? new InvalidOperationException("simulated transient failure")
                : new NotSupportedException("simulated distinct failure"));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = [];

        public void Report(string operation, Exception exception) => Reports.Add((operation, exception));
    }
}
