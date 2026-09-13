using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Presentation;

/// <summary>
/// Pushes session-lock and fullscreen transitions into a
/// <see cref="PresentationCoordinator"/> without exposing
/// <c>AppLifecycleCoordinator</c>'s private state. Pause is read directly
/// from the pause store on each decision instead of being pushed.
/// </summary>
public interface IPresentationEnvironmentSink
{
    void SetSessionLocked(bool locked);

    void SetFullscreen(bool fullscreen);
}

/// <summary>
/// The one presentation gateway. Every unsolicited event (a remote note
/// arriving, a reminder becoming due) passes through
/// <see cref="PublishAsync"/>; <see cref="TickAsync"/> drains at most one
/// queued item per call, driven by the existing 30-second reminder
/// scheduler via <see cref="AppHost"/> — there is no new timer. Explicit
/// user actions never go through this type; they stay on
/// <see cref="PetPresentationCoordinator"/>.
/// </summary>
public sealed class PresentationCoordinator :
    IAppHostPresentationGateway,
    IPresentationEnvironmentSink
{
    private readonly PresentationPolicy _policy;
    private readonly INotificationService _notifications;
    private readonly PetStateMachine _pet;
    private readonly Func<PetPresentation, AnimationOptions, CancellationToken, Task> _playAsync;
    private readonly Func<AnimationOptions> _options;
    private readonly Func<bool> _isQuietHours;
    private readonly Func<PauseState> _pauseState;
    private readonly object _gate = new();
    private bool _sessionLocked;
    private bool _fullscreen;

    public PresentationCoordinator(
        PresentationPolicy policy,
        INotificationService notifications,
        PetStateMachine pet,
        Func<PetPresentation, AnimationOptions, CancellationToken, Task> playAsync,
        Func<AnimationOptions> options,
        Func<bool> isQuietHours,
        Func<PauseState> pauseState)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isQuietHours = isQuietHours ?? throw new ArgumentNullException(nameof(isQuietHours));
        _pauseState = pauseState ?? throw new ArgumentNullException(nameof(pauseState));
    }

    public void SetSessionLocked(bool locked)
    {
        lock (_gate)
        {
            _sessionLocked = locked;
        }
    }

    public void SetFullscreen(bool fullscreen)
    {
        lock (_gate)
        {
            _fullscreen = fullscreen;
        }
    }

    /// <summary>
    /// Registers Windows app notifications. A registration failure is
    /// swallowed here: it must never throw out of startup, and durable
    /// events still reach the user through the pet bubble fallback.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_notifications is not IRegistrableNotificationService registrable)
        {
            return;
        }

        try
        {
            await registrable.TryRegisterAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu notification registration failed: {0}", exception);
        }
    }

    /// <summary>
    /// Publishes a newly-arrived durable item. Bypass items (reminders with
    /// <see cref="QuietHoursBehavior.DeliverImmediately"/>) are presented
    /// immediately regardless of the environment; everything else is queued
    /// while quiet hours, focus, fullscreen, a locked session, or a pause is
    /// active, and presented immediately otherwise.
    /// </summary>
    public async Task PublishAsync(
        DurableNotification item,
        bool bypassSuppression,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = DateTimeOffset.UtcNow;
        var suppressed = !bypassSuppression && IsSuppressed(CaptureEnvironment(now));
        if (suppressed)
        {
            _policy.Enqueue(item);
            return;
        }

        _policy.RecordImmediateRelease(now);
        await PresentAsync(item, cancellationToken);
    }

    /// <summary>
    /// Drains at most one queued durable item, called after each successful
    /// reminder tick by <see cref="AppHost"/> — no new timer is introduced.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var environment = CaptureEnvironment(now);
        var decision = _policy.Decide(
            environment.NowQuiet,
            environment.Fullscreen,
            environment.Paused,
            environment.SessionLocked,
            environment.FocusActive,
            now);

        foreach (var item in decision.ToPresent)
        {
            await PresentAsync(item, cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task PresentAsync(DurableNotification item, CancellationToken cancellationToken)
    {
        var petEvent = ToPetEvent(item);
        var presentation = _pet.Handle(petEvent);
        await ObserveAsync(
            _playAsync(presentation, _options(), cancellationToken),
            "presentation-playback");
        await ObserveAsync(
            ShowNotificationAsync(item, cancellationToken),
            "presentation-notification");
    }

    private Task ShowNotificationAsync(DurableNotification item, CancellationToken cancellationToken) =>
        item.Kind switch
        {
            PresentationItemKind.RemoteNote => _notifications.ShowRemoteNoteArrivalAsync(
                Guid.ParseExact(item.Id, "D"),
                cancellationToken),
            PresentationItemKind.Reminder => _notifications.ShowReminderAsync(
                item.Id,
                item.Title ?? string.Empty,
                cancellationToken),
            _ => Task.CompletedTask,
        };

    private static PetEvent ToPetEvent(DurableNotification item) => item.Kind switch
    {
        PresentationItemKind.RemoteNote => new PetEvent.RemoteNoteArrived(item.Id),
        PresentationItemKind.Reminder => new PetEvent.ReminderDue(item.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unsupported presentation item kind."),
    };

    private SuppressionSnapshot CaptureEnvironment(DateTimeOffset now)
    {
        bool sessionLocked;
        bool fullscreen;
        lock (_gate)
        {
            sessionLocked = _sessionLocked;
            fullscreen = _fullscreen;
        }

        var paused = PausePolicy.IsSuppressed(_pauseState(), now, fullscreen);
        var focusActive = _pet.Current.State == PetState.Focus;
        return new SuppressionSnapshot(_isQuietHours(), fullscreen, paused, sessionLocked, focusActive);
    }

    private static bool IsSuppressed(SuppressionSnapshot snapshot) =>
        snapshot.NowQuiet || snapshot.Fullscreen || snapshot.Paused
        || snapshot.SessionLocked || snapshot.FocusActive;

    private static async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu {0} failed: {1}", operation, exception);
        }
    }

    private readonly record struct SuppressionSnapshot(
        bool NowQuiet,
        bool Fullscreen,
        bool Paused,
        bool SessionLocked,
        bool FocusActive);
}
