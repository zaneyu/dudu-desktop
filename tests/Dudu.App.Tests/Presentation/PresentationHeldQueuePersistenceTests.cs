using Dudu.App.Animation;
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
            DateTimeOffset.Parse("2026-09-19T08:00:00Z")));
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
            DateTimeOffset.Parse("2026-09-18T20:00:00Z")));
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

    private sealed class RecordingNotificationService : INotificationService
    {
        public Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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
}
