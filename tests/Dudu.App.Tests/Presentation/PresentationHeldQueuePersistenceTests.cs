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
    public async Task A_coordinator_started_user_hidden_holds_an_item_until_shown_then_presents_it_once()
    {
        // Finding B: production must start this gateway user-hidden until
        // the lifecycle coordinator pushes the first real value -- the
        // startup reminder tick still advances the reminder engine before
        // the events sink / startup visibility gate have run, so without
        // this, a due reminder could publish while the coordinator still
        // believed the unset default ("visible") was real, animating it
        // into a window that is not shown yet and deleting its row on that
        // "successful" presentation.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var played = 0;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository,
            initialUserHidden: true);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        // Held purely because the coordinator started user-hidden -- quiet
        // hours/fullscreen/lock/pause are all clear. A hold caused only by
        // user-hidden still toasts immediately (see PublishAsync's toastNow)
        // and persists the row already marked toasted.
        Assert.Equal(0, played);
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.True(Assert.Single(repository.Rows.Values).Toasted);

        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_later_occurrence_of_an_already_queued_item_still_toasts_once_hiding_dudu_is_the_only_hold_reason()
    {
        // Finding 12: while an earlier occurrence of a recurring reminder is
        // held only by quiet hours, a later occurrence's PublishAsync call
        // hits the already-queued early return and (before this fix) got no
        // toast at all -- for as long as the hold lasted. Once quiet hours
        // ends but she has since hidden Dudu from the tray -- so hiding Dudu
        // becomes the sole reason anything is still held -- that later
        // occurrence must still toast, exactly like the toastNow path for a
        // brand new hold.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var played = 0;
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { played++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        // Held purely by quiet hours -- not by hiding Dudu -- so the first
        // hold does not toast yet.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Equal(0, notifications.ReminderCalls);
        Assert.Single(repository.Rows);

        // Quiet hours ends, but she hides Dudu right after -- the item is
        // still sitting in the queue (nothing has ticked/released it), and
        // hiding Dudu is now the only reason it remains held.
        quiet = false;
        coordinator.SetUserVisible(false);

        // A later occurrence of the same reminder (same Kind:Id key)
        // publishes while the earlier one is still queued.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Equal(0, played);
        // Still only one held row -- the later occurrence does not queue a
        // second entry, it only toasts.
        Assert.Single(repository.Rows);

        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, played);
        // No second toast on the eventual release -- it is still the same
        // held row, already marked toasted by the later-occurrence toast
        // above.
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_later_occurrence_toast_while_already_queued_is_persisted_and_survives_a_restart()
    {
        // Finding 6: PublishAsync's already-queued + toastNow branch (see
        // the sibling test above) only marked _toastedWhileHeldIds in
        // memory and never persisted it -- a restart before the held row
        // was ever released would reload it as untoasted and show the same
        // Windows toast a second time. Mirrors what PresentAsync's own
        // toastShown-and-not-succeeded path already does: persist the
        // toasted flag right after showing the toast.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        // Held purely by quiet hours, same setup as the sibling test.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.False(repository.Rows.Values.Single().Toasted);

        quiet = false;
        coordinator.SetUserVisible(false);

        // The later-occurrence toast fires (hiding Dudu is now the sole
        // hold reason) -- the persisted row's Toasted flag must flip to
        // true right along with it, not just the in-memory marker.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        var row = Assert.Single(repository.Rows.Values);
        Assert.True(row.Toasted, "The already-queued toast must be persisted, not just marked in memory.");

        // Simulate a restart: a fresh coordinator over the same durable
        // repository must not re-toast the reloaded, already-toasted row.
        var restartedPlayed = 0;
        var restarted = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => { restartedPlayed++; return Task.CompletedTask; },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository,
            initialUserHidden: true);
        await restarted.StartAsync(CancellationToken.None);

        restarted.SetUserVisible(true);
        await restarted.TickAsync(CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        // The reloaded row was actually released and presented (not just
        // skipped as "already toasted" and left dangling): the pet
        // animation ran once, and its row is gone from the repository.
        Assert.Equal(1, restartedPlayed);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_failed_already_queued_toast_is_not_marked_toasted_and_is_retried_on_release()
    {
        // Finding: PublishAsync's already-queued + toastNow branch used to
        // discard ObserveAsync's success result and persist the toasted
        // marker unconditionally -- a toast that actually failed to show
        // was still recorded as shown, in memory and on the durable row,
        // and the real Windows toast was never attempted again once the
        // held item was finally released.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new ThrowOnFirstCallNotificationService();
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        // Held purely by quiet hours, same setup as the sibling tests.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.False(repository.Rows.Values.Single().Toasted);

        quiet = false;
        coordinator.SetUserVisible(false);

        // The later-occurrence toast fires, but the notification service
        // throws -- the persisted row must stay untoasted, not be marked
        // toasted despite the failure.
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.False(
            repository.Rows.Values.Single().Toasted,
            "A failed toast must not be persisted as toasted.");

        // She un-hides Dudu, releasing the held item: since the failed
        // toast was never marked toasted (in memory or persisted), the
        // release path must attempt the toast again -- not skip it as
        // already shown.
        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(2, notifications.ReminderCalls);
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
    public async Task A_repeatedly_failing_animation_does_not_retoast_and_marks_the_held_row_toasted()
    {
        // Finding 1: PresentAsync showed the toast successfully but never
        // recorded that in _toastedWhileHeldIds when the animation itself
        // kept failing, so the requeued item re-toasted on every later
        // attempt -- every 30 s tick, and (since the persisted row's Toasted
        // column stayed false) across a restart too, for up to MaxHeldAge.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.True(Assert.Single(repository.Rows.Values).Toasted);

        // Every later attempt keeps failing the animation but must not
        // re-toast.
        await coordinator.TickAsync(CancellationToken.None);
        await coordinator.TickAsync(CancellationToken.None);
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.True(Assert.Single(repository.Rows.Values).Toasted);
    }

    [Fact]
    public async Task A_failed_release_of_a_previously_held_item_persists_the_toasted_flag_immediately()
    {
        // Finding D: the marker PresentAsync sets in _toastedWhileHeldIds is
        // memory-only on TickAsync's decline path -- that path deliberately
        // does not re-persist the whole row (to preserve QueuedUtc). Without
        // MarkToastedAsync, a restart before the retry finally succeeds
        // would reload the row as untoasted and show the Windows toast a
        // second time.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);

        // Held by quiet hours (not user-hidden), so PublishAsync's own
        // toastNow shortcut never fires -- the row persists untoasted.
        Assert.Equal(0, notifications.ReminderCalls);
        Assert.False(Assert.Single(repository.Rows.Values).Toasted);

        // Quiet hours end: TickAsync releases it, the toast fires for the
        // first time inside PresentAsync, but the animation keeps failing
        // so the item is requeued via TickAsync's decline path -- which
        // does not re-persist the row wholesale.
        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, notifications.ReminderCalls);
        Assert.True(Assert.Single(repository.Rows.Values).Toasted);
    }

    [Fact]
    public async Task A_failed_release_of_a_previously_held_local_note_does_not_persist_a_toasted_flag()
    {
        // Finding D: ShowNotificationAsync shows no real toast for LocalNote
        // (it falls through to Task.CompletedTask), so persisting the
        // toasted flag for it would be recording something that never
        // actually happened.
        var repository = new RecordingHeldPresentationRepository();
        var quiet = true;
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new CountingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.LocalNote(new LocalLoveNote("note-1", "Hi"), "wave"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.False(Assert.Single(repository.Rows.Values).Toasted);

        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);

        Assert.False(Assert.Single(repository.Rows.Values).Toasted);
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
    public async Task DiscardHeldByKindAsync_removes_every_queued_item_of_that_kind_but_leaves_others()
    {
        // Finding 6(b): forgetting a broken pairing deletes every unopened
        // remote note locally in one pass (RemoteSyncService.ForgetPairingLocallyAsync
        // -> IRemoteEnvelopeRepository.DeleteAllAsync), so any RemoteNote
        // still queued or held for later ambient presentation must go with
        // them -- but a held Reminder must be left completely alone.
        var repository = new RecordingHeldPresentationRepository();
        var notifications = new CountingNotificationService();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        // Held for the same reason as the sibling test above: user-hidden
        // makes the toast fire immediately while the item still sits queued
        // and persisted, so both the queue and the repository can be
        // asserted on afterward.
        coordinator.SetUserVisible(false);

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
            bypassSuppression: false,
            CancellationToken.None);
        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
            bypassSuppression: false,
            CancellationToken.None);
        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Equal(3, policy.QueuedCount);
        Assert.Equal(3, repository.Rows.Count);

        await coordinator.DiscardHeldByKindAsync(PresentationItemKind.RemoteNote, CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
        Assert.Single(repository.Rows);
        Assert.Equal("Reminder:reminder-1", repository.Rows.Single().Key);

        // Both queued-while-hidden publishes above already toasted
        // immediately (PublishAsync's toastNow, same as the sibling test):
        // RemoteNoteCalls is 2 before the discard even runs. If a discarded
        // remote note's queue entry or row survived, un-hiding and ticking
        // would present it (or requeue-and-repersist it) and show a further
        // toast on top of those two -- so the count staying at 2 here is
        // what proves nothing survived the discard, not 0.
        Assert.Equal(2, notifications.RemoteNoteCalls);
        coordinator.SetUserVisible(true);
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(2, notifications.RemoteNoteCalls);
    }

    [Fact]
    public async Task DiscardHeldByKindAsync_during_an_in_flight_presentation_drops_it_for_good_if_it_then_fails()
    {
        // Finding E: DiscardHeldByKindAsync only ever saw
        // PresentationPolicy's in-memory queue -- an item of that kind
        // already dequeued into _presentingIds for an in-flight
        // presentation was invisible to it entirely, so a subsequent
        // failure could still requeue and re-persist it.
        var repository = new RecordingHeldPresentationRepository();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var quiet = true;
        var playStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageId = Guid.NewGuid().ToString("D");
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                playStarted.TrySetResult();
                await releasePlay.Task;
                throw new InvalidOperationException("playback failed");
            },
            () => AnimationOptions.Default,
            isQuietHours: () => quiet,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.RemoteNote(messageId),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Single(repository.Rows);

        quiet = false;
        var tick = coordinator.TickAsync(CancellationToken.None);
        await playStarted.Task;

        await coordinator.DiscardHeldByKindAsync(PresentationItemKind.RemoteNote, CancellationToken.None);

        releasePlay.SetResult();
        await tick;

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task DiscardHeldByKindAsync_is_a_no_op_when_nothing_of_that_kind_is_queued()
    {
        var repository = new RecordingHeldPresentationRepository();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new CountingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.DiscardHeldByKindAsync(PresentationItemKind.RemoteNote, CancellationToken.None);

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task DiscardHeldAsync_is_a_no_op_for_a_null_or_blank_id()
    {
        // Finding F: DiscardHeldAsync's caller has already committed its own
        // delete/consume by the time this runs (e.g. LoveNotesViewModel
        // deletes the note, then tells this gateway to drop its held copy).
        // Throwing for a blank id would turn an already-successful user
        // action into a visible error, for an id that never had anything
        // held for it anyway.
        var repository = new RecordingHeldPresentationRepository();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new CountingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, null!, CancellationToken.None);
        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "", CancellationToken.None);
        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "   ", CancellationToken.None);

        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_failed_immediate_present_retry_does_not_leave_the_row_behind_after_a_concurrent_discard()
    {
        // Finding 7: PublishAsync's failed-immediate-present path holds its
        // own _presentingIds entry for the whole RequeueHeldAsync/
        // PersistHeldAsync call (removed only in its `finally`, after this
        // returns), so the old "still relevant" guard -- which counted ANY
        // _presentingIds membership, even the caller's own -- could never
        // actually fire for this path. If a concurrent DiscardHeldAsync
        // (e.g. she completed the reminder from the Reminders page while
        // this retry was mid-persist) took the item out of the queue for
        // good during that window, the guard still saw it as "relevant" and
        // kept the stale row, which SaveAsync's own delayed write then
        // recreates anyway -- an orphan that would reload and re-present on
        // the next launch even though it was already handled.
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new BlockingSaveHeldPresentationRepository(saveStarted, releaseSave.Task);
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(new InvalidOperationException("playback failed")),
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        var item = DurableNotification.Reminder("reminder-1", "Stretch");

        // The immediate present attempt fails, so PublishAsync requeues and
        // persists the item; block mid-save.
        var publish = coordinator.PublishAsync(item, bypassSuppression: false, CancellationToken.None);
        await saveStarted.Task;

        // While that save is still in flight, the reminder is separately
        // completed from the Reminders page (never goes through
        // PresentAsync at all), discarding this same held item for good.
        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "reminder-1", CancellationToken.None);

        // Let the blocked save land -- it writes unconditionally, so the row
        // reappears regardless of the discard above; only the post-save
        // guard decides whether it survives.
        releaseSave.SetResult();
        await publish;

        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_discard_during_an_in_flight_immediate_presentation_drops_the_item_for_good_if_it_then_fails()
    {
        // Finding E: DiscardHeldAsync used to ignore _presentingIds
        // entirely, so discarding an item (e.g. completing the reminder
        // from the Reminders page) while PublishAsync's own immediate-
        // present attempt for that same item was still in flight did not
        // stop a subsequent failure from requeuing and re-persisting it --
        // resurrecting something already handled.
        var repository = new RecordingHeldPresentationRepository();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var playStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                playStarted.TrySetResult();
                await releasePlay.Task;
                throw new InvalidOperationException("playback failed");
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);

        var publish = coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        await playStarted.Task;

        // Completed from the Reminders page while the immediate attempt
        // above is still in flight.
        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "reminder-1", CancellationToken.None);

        releasePlay.SetResult();
        await publish;

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);

        // If it were still queued/held, this tick would present it again.
        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task A_discard_during_an_in_flight_tick_release_drops_the_item_for_good_if_it_then_fails()
    {
        // Same as above but for TickAsync's own release of an already-held
        // item, exercising TickAsync's decline path rather than
        // PublishAsync's immediate-present failure path.
        var repository = new RecordingHeldPresentationRepository();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var quiet = true;
        var playStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                playStarted.TrySetResult();
                await releasePlay.Task;
                throw new InvalidOperationException("playback failed");
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

        quiet = false;
        var tick = coordinator.TickAsync(CancellationToken.None);
        await playStarted.Task;

        await coordinator.DiscardHeldAsync(PresentationItemKind.Reminder, "reminder-1", CancellationToken.None);

        releasePlay.SetResult();
        await tick;

        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);

        await coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);
        Assert.Empty(repository.Rows);
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
        // Finding 8 (test bug): RemoteNote's factory requires a protocol-safe
        // "D"-format GUID and throws on anything else (see
        // PresentationPolicy.RemoteNote) -- "msg-1" is not one.
        Assert.Equal(
            $"RemoteNote:{Guid.Empty:D}",
            DurableNotification.RemoteNote(Guid.Empty.ToString("D")).Key);
        Assert.Equal("Reminder:reminder-1", DurableNotification.Reminder("reminder-1", "Stretch").Key);
        Assert.Equal(
            "LocalNote:note-1",
            DurableNotification.LocalNote(new LocalLoveNote("note-1", "Hi"), "wave").Key);
    }

    [Fact]
    public async Task TickAsync_requeues_a_released_item_already_presenting_elsewhere_instead_of_dropping_it()
    {
        // Finding 19: Decide() releases at most one item per call. If that
        // item's key is already in _presentingIds (a concurrent presenter
        // has it in flight), TickAsync excludes it from toPresent -- but it
        // has already been dequeued from PresentationPolicy by Decide(), so
        // it used to just vanish: not requeued, and its row separately
        // deleted by the old purge-style handling. It must be put back in
        // the queue instead, to be reconsidered on a later tick.
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var repository = new RecordingHeldPresentationRepository();
        var playbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playCount = 0;
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            async (_, _, _) =>
            {
                Interlocked.Increment(ref playCount);
                await playbackGate.Task;
            },
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            heldPresentations: repository);
        var item = DurableNotification.Reminder("reminder-1", "Stretch");
        policy.Enqueue(item);
        await repository.SaveAsync(
            new HeldPresentation(
                item.Key, "Reminder", "reminder-1", "Stretch", null, null, null,
                DateTimeOffset.Parse("2026-09-19T08:00:00Z"), Toasted: false),
            CancellationToken.None);

        // First tick dequeues the item and starts presenting it (blocked, so
        // it stays "currently presenting" -- its key is in _presentingIds).
        var firstTick = coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, policy.QueuedCount);

        // Simulate the race this fix guards: the item ends up back in the
        // in-memory queue (as a failed concurrent presenter's own requeue
        // would do) while its key is still marked "presenting" by the
        // still-in-flight first tick above.
        policy.Requeue(item);

        // A second tick's Decide() dequeues this same item again and must
        // find its key already in _presentingIds.
        await coordinator.TickAsync(CancellationToken.None);

        // Requeued, not dropped: back in the queue for a later tick, and its
        // persisted row was left alone (not deleted).
        Assert.Equal(1, policy.QueuedCount);
        Assert.Single(repository.Rows);

        // Let the original in-flight presentation finish successfully.
        playbackGate.SetResult();
        await firstTick;
        Assert.Equal(1, playCount);
    }

    [Fact]
    public async Task StartAsync_drops_a_row_whose_decoded_key_collides_with_another_without_leaking_its_toasted_marker()
    {
        // Finding 18: HeldPresentationRepository.SaveAsync upserts on its own
        // primary key, so two rows can never literally share a
        // presentation_key -- but two rows with DIFFERENT primary keys can
        // still decode (via ToDurableNotification) to the same Kind:Id
        // notification key, and PresentationPolicy.Enqueue correctly refuses
        // the second one as a duplicate. LoadHeldItemsAsync used to ignore
        // that false return: it still latched the refused row's Toasted flag
        // into _toastedWhileHeldIds (an orphaned marker nothing will ever
        // consume) and left its row on disk to fail the same way forever.
        var queuedUtc = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var first = new HeldPresentation(
            "Reminder:reminder-1#a", "Reminder", "reminder-1", "Stretch", null, null, null,
            queuedUtc, Toasted: false);
        var duplicate = first with { Key = "Reminder:reminder-1#b", Toasted = true };
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(first);
        repository.Seed(duplicate);
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var notifications = new CountingNotificationService();
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
            utcNow: () => queuedUtc.AddMinutes(5),
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
        // Only one of the two colliding rows survives on disk -- the refused
        // duplicate must have been deleted, not left behind.
        Assert.Single(repository.Rows);

        await coordinator.TickAsync(CancellationToken.None);

        // If the refused row's Toasted=true marker had leaked into
        // _toastedWhileHeldIds, this toast would have been silently
        // swallowed regardless of which row actually survived.
        Assert.Equal(1, notifications.ReminderCalls);
        Assert.Equal(1, played);
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

    [Fact]
    public async Task StartAsync_drops_and_deletes_an_eight_day_old_held_row_but_keeps_a_six_day_old_one()
    {
        // Finding 20 (part 1): MaxHeldAge (7 days) is the only bound on "the
        // same reminder shown forever" (e.g. from a toast that keeps failing
        // and requeuing) -- nothing previously exercised the actual
        // comparison at either side of the boundary.
        var now = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var repository = new RecordingHeldPresentationRepository();
        repository.Seed(new HeldPresentation(
            "Reminder:old", "Reminder", "old", "Stale", null, null, null,
            now - TimeSpan.FromDays(8), Toasted: false));
        repository.Seed(new HeldPresentation(
            "Reminder:fresh", "Reminder", "fresh", "Recent", null, null, null,
            now - TimeSpan.FromDays(6), Toasted: false));
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => true,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => now,
            heldPresentations: repository);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, policy.QueuedCount);
        var remaining = Assert.Single(repository.Rows.Values);
        Assert.Equal("Reminder:fresh", remaining.Key);
    }

    [Fact]
    public async Task A_failed_release_leaves_the_held_rows_QueuedUtc_unchanged()
    {
        // Finding 20 (part 2): RequeueHeldAsync is documented as PublishAsync
        // -only -- TickAsync's own decline path deliberately does not
        // re-persist a failed retry, so the row's original QueuedUtc is what
        // MaxHeldAge measures actual age against, not time-since-last-retry.
        var originalQueuedUtc = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var repository = new RecordingHeldPresentationRepository();
        var attempt = 0;
        var quiet = true;
        var now = originalQueuedUtc;
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
            utcNow: () => now,
            heldPresentations: repository);

        await coordinator.PublishAsync(
            DurableNotification.Reminder("reminder-1", "Stretch"),
            bypassSuppression: false,
            CancellationToken.None);
        Assert.Equal(originalQueuedUtc, Assert.Single(repository.Rows.Values).QueuedUtc);

        // Time passes, then a released retry fails.
        now = originalQueuedUtc.AddHours(3);
        quiet = false;
        await coordinator.TickAsync(CancellationToken.None);

        var afterFailedRetry = Assert.Single(repository.Rows.Values);
        Assert.Equal(originalQueuedUtc, afterFailedRetry.QueuedUtc);
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
        public int RemoteNoteCalls { get; private set; }

        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
        {
            ReminderCalls++;
            return Task.CompletedTask;
        }

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken)
        {
            RemoteNoteCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowOnFirstCallNotificationService : INotificationService
    {
        public int ReminderCalls { get; private set; }

        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
        {
            ReminderCalls++;
            if (ReminderCalls == 1)
            {
                throw new InvalidOperationException("simulated toast failure");
            }

            return Task.CompletedTask;
        }

        public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    // Finding 17: this fake's Dictionary is mutated from concurrent tasks in
    // tests like TickAsync_requeues_a_released_item_already_presenting_elsewhere_instead_of_dropping_it
    // and DiscardHeldByKindAsync_during_an_in_flight_presentation_drops_it_for_good_if_it_then_fails,
    // which deliberately run a publish/tick and a discard/second-tick
    // concurrently -- Dictionary itself gives no thread-safety guarantee
    // once more than one thread touches it, even for reads racing a write.
    private sealed class RecordingHeldPresentationRepository : IHeldPresentationRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, HeldPresentation> _rows = [];

        public IReadOnlyDictionary<string, HeldPresentation> Rows
        {
            get { lock (_sync) return new Dictionary<string, HeldPresentation>(_rows); }
        }

        public void Seed(HeldPresentation item)
        {
            lock (_sync) _rows[item.Key] = item;
        }

        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken)
        {
            lock (_sync) return Task.FromResult<IReadOnlyList<HeldPresentation>>(_rows.Values.ToArray());
        }

        public Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
        {
            lock (_sync) _rows[item.Key] = item;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            lock (_sync) _rows.Remove(key);
            return Task.CompletedTask;
        }

        public Task MarkToastedAsync(string key, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_rows.TryGetValue(key, out var existing))
                {
                    _rows[key] = existing with { Toasted = true };
                }
            }

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

        public Task MarkToastedAsync(string key, CancellationToken cancellationToken) =>
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
        private readonly object _sync = new();
        private readonly Dictionary<string, HeldPresentation> _rows = [];

        // Finding 17: the test that uses this fake deliberately overlaps a
        // blocked SaveAsync with a concurrent TickAsync's own DeleteAsync/
        // read of these rows -- guard every access the same way the other
        // fakes now do.
        public IReadOnlyDictionary<string, HeldPresentation> Rows
        {
            get { lock (_sync) return new Dictionary<string, HeldPresentation>(_rows); }
        }

        public Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken)
        {
            lock (_sync) return Task.FromResult<IReadOnlyList<HeldPresentation>>(_rows.Values.ToArray());
        }

        public async Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release;
            lock (_sync) _rows[item.Key] = item;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            lock (_sync) _rows.Remove(key);
            return Task.CompletedTask;
        }

        public Task MarkToastedAsync(string key, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_rows.TryGetValue(key, out var existing))
                {
                    _rows[key] = existing with { Toasted = true };
                }
            }

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

        public Task MarkToastedAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = [];

        public void Report(string operation, Exception exception) => Reports.Add((operation, exception));
    }
}
