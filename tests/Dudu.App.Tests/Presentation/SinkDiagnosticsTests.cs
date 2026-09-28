using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Presentation;

/// <summary>
/// P2 sink visibility: presentation/notification catch-alls route through
/// the shared <see cref="IAppHostErrorReporter"/> with fixed operation
/// names carrying the exception type only — never note or toast
/// content. Durability behavior (no rollback, no broken loops) is unchanged.
/// </summary>
public sealed class SinkDiagnosticsTests
{
    private static readonly DateTimeOffset DueUtc =
        DateTimeOffset.Parse("2026-09-17T20:00:00Z");

    [Fact]
    public async Task Remote_note_sink_failure_reports_remote_note_notify_without_throwing()
    {
        var failure = new InvalidOperationException("gateway down");
        var reporter = new RecordingErrorReporter();
        var sink = new RemoteNoteArrivalSink(
            () => new ThrowingGateway(failure),
            reporter);

        // Must not throw: the envelope is already durably recorded and the
        // poll loop must continue. The message id itself is never logged.
        await sink.NotifyAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("remote-note-notify", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    [Fact]
    public async Task Remote_note_sink_propagates_when_the_gateway_is_not_ready()
    {
        // Unlike a genuine presentation failure (above), a gateway that is not
        // resolvable yet (safe mode, or a startup race) must propagate so
        // RemoteSyncService's poll loop does not acknowledge the envelope off
        // the relay before ever attempting to show it.
        var failure = new InvalidOperationException("The presentation gateway is not ready.");
        var reporter = new RecordingErrorReporter();
        var sink = new RemoteNoteArrivalSink(
            () => throw failure,
            reporter);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.NotifyAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Presentation_playback_failure_reports_presentation_tick_and_requeues()
    {
        var failure = new InvalidOperationException("playback down");
        var reporter = new RecordingErrorReporter();
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var coordinator = new PresentationCoordinator(
            policy,
            new RecordingNotificationService(),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.FromException(failure),
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            utcNow: () => DueUtc,
            errorReporter: reporter);
        var item = DurableNotification.RemoteNote("11111111-1111-4111-8111-111111111111");

        // Must not throw: the item is requeued for the next tick.
        await coordinator.PublishAsync(item, bypassSuppression: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, policy.QueuedCount);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal("presentation-tick", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    [Fact]
    public async Task Presentation_start_failure_reports_presentation_tick_without_throwing()
    {
        var failure = new InvalidOperationException("registration down");
        var reporter = new RecordingErrorReporter();
        var coordinator = new PresentationCoordinator(
            new PresentationPolicy(TimeSpan.Zero),
            new FailingRegistrableNotifications(failure),
            PetStateMachine.CreateIdle(),
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            isQuietHours: () => false,
            pauseState: () => PauseState.None,
            petGate: new SemaphoreSlim(1, 1),
            errorReporter: reporter);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("presentation-tick", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    [Fact]
    public async Task Toast_show_failure_reports_toast_notify_and_disables_notifications()
    {
        var failure = new InvalidOperationException("toast down");
        var reporter = new RecordingErrorReporter();
        var service = new AppNotificationService(
            new ThrowingSink(failure),
            errorReporter: reporter);

        await service.ShowRemoteNoteArrivalAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(service.NotificationsAvailable);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal("toast-notify", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    [Fact]
    public async Task Toast_register_failure_reports_toast_notify_and_returns_false()
    {
        var failure = new InvalidOperationException("register down");
        var reporter = new RecordingErrorReporter();
        var service = new AppNotificationService(
            new ThrowingRegisterSink(failure),
            errorReporter: reporter);

        Assert.False(await service.TryRegisterAsync(TestContext.Current.CancellationToken));
        Assert.False(service.NotificationsAvailable);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal("toast-notify", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    public sealed record ErrorReport(string Operation, Exception Exception);

    public sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        private readonly object _sync = new();

        public List<ErrorReport> Reports { get; } = [];

        public void Report(string operation, Exception exception)
        {
            lock (_sync)
            {
                Reports.Add(new ErrorReport(operation, exception));
            }
        }
    }

    private sealed class ThrowingGateway(Exception failure) : IUnsolicitedPresentationGateway
    {
        public Task PublishAsync(
            DurableNotification item,
            bool bypassSuppression,
            CancellationToken cancellationToken = default) =>
            Task.FromException(failure);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public Task ShowRemoteNoteArrivalAsync(
            Guid messageId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingRegistrableNotifications(Exception failure)
        : INotificationService, IRegistrableNotificationService
    {
        public Task<bool> TryRegisterAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(failure);

        public Task ShowRemoteNoteArrivalAsync(
            Guid messageId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingSink(Exception failure) : INotificationSink
    {
        public Task<bool> TryRegisterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public Task RemoveAsync(string tag, string group, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingRegisterSink(Exception failure) : INotificationSink
    {
        public Task<bool> TryRegisterAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(failure);

        public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RemoveAsync(string tag, string group, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
