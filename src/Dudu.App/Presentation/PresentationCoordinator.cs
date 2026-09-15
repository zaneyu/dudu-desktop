using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Notes;
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
/// arriving, a reminder becoming due, or an automatically selected local
/// note) passes through
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
    private readonly SemaphoreSlim _petGate;
    private readonly AmbientScheduler? _ambientScheduler;
    private readonly LocalNoteSelector? _localNoteSelector;
    private readonly object _gate = new();
    private readonly HashSet<string> _presentingIds = new(StringComparer.Ordinal);
    private readonly Func<bool> _isFullscreenNow;
    private bool _sessionLocked;
    private bool _fullscreen;

    /// <param name="petGate">
    /// The same <see cref="SemaphoreSlim"/> instance given to
    /// <see cref="PetPresentationCoordinator"/> in production, so an
    /// explicit user one-shot and an unsolicited release can never interleave
    /// their mutation of the shared <see cref="PetStateMachine"/>.
    /// </param>
    public PresentationCoordinator(
        PresentationPolicy policy,
        INotificationService notifications,
        PetStateMachine pet,
        Func<PetPresentation, AnimationOptions, CancellationToken, Task> playAsync,
        Func<AnimationOptions> options,
        Func<bool> isQuietHours,
        Func<PauseState> pauseState,
        SemaphoreSlim petGate,
        AmbientScheduler? ambientScheduler = null,
        LocalNoteSelector? localNoteSelector = null,
        Func<bool>? isFullscreenNow = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isQuietHours = isQuietHours ?? throw new ArgumentNullException(nameof(isQuietHours));
        _pauseState = pauseState ?? throw new ArgumentNullException(nameof(pauseState));
        _petGate = petGate ?? throw new ArgumentNullException(nameof(petGate));
        _ambientScheduler = ambientScheduler;
        _localNoteSelector = localNoteSelector;
        _isFullscreenNow = isFullscreenNow ?? (() => { lock (_gate) return _fullscreen; });
        if ((_ambientScheduler is null) != (_localNoteSelector is null))
        {
            throw new ArgumentException(
                "The ambient scheduler and local-note selector must be supplied together.",
                nameof(ambientScheduler));
        }
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
    /// active, and presented immediately otherwise. An item whose kind+id is
    /// already queued or is currently being presented is a duplicate and is
    /// dropped rather than queued or presented a second time — this is what
    /// makes the gateway safe for more than one concurrent unsolicited
    /// source (e.g. a reminder tick and a remote-note poller) on its own,
    /// without relying on a caller to serialize them.
    /// </summary>
    public async Task PublishAsync(
        DurableNotification item,
        bool bypassSuppression,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = DateTimeOffset.UtcNow;
        bool shouldPresentNow;
        lock (_gate)
        {
            if (_presentingIds.Contains(item.Key) || _policy.IsQueued(item))
            {
                return;
            }

            shouldPresentNow = bypassSuppression || !IsSuppressed(CaptureEnvironment(now));
            if (!shouldPresentNow)
            {
                _policy.Enqueue(item);
                return;
            }

            _policy.RecordImmediateRelease(now);
            _presentingIds.Add(item.Key);
        }

        try
        {
            if (!await PresentAsync(item, cancellationToken))
            {
                _policy.Requeue(item);
            }
        }
        catch
        {
            _policy.Requeue(item);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _presentingIds.Remove(item.Key);
            }
        }
    }

    /// <summary>
    /// Drains at most one queued durable item, called after each successful
    /// reminder tick by <see cref="AppHost"/> — no new timer is introduced.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        DurableNotification? toPresent;
        SuppressionSnapshot environment;
        lock (_gate)
        {
            environment = CaptureEnvironment(now);
            var decision = _policy.Decide(
                environment.NowQuiet,
                environment.Fullscreen,
                environment.Paused,
                environment.SessionLocked,
                environment.FocusActive,
                now);

            toPresent = decision.ToPresent.FirstOrDefault(item => !_presentingIds.Contains(item.Key));
            if (toPresent is not null)
            {
                _presentingIds.Add(toPresent.Key);
            }
        }

        if (toPresent is not null)
        {
            try
            {
                if (!await PresentAsync(toPresent, cancellationToken))
                {
                    _policy.Requeue(toPresent);
                }
            }
            catch
            {
                _policy.Requeue(toPresent);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _presentingIds.Remove(toPresent.Key);
                }
            }

            return;
        }

        if (_ambientScheduler is null
            || _localNoteSelector is null
            || environment.NowQuiet
            || environment.Fullscreen
            || environment.Paused
            || environment.SessionLocked
            || environment.FocusActive)
        {
            return;
        }

        var ambient = _ambientScheduler.TryGetNextEvent(
            environment.Paused,
            environment.FocusActive,
            environment.Fullscreen,
            environment.SessionLocked,
            environment.NowQuiet);
        if (ambient is not PetEvent.AmbientRequested ambientRequest)
        {
            return;
        }

        var note = await _localNoteSelector.SelectAsync(
            manualRequest: false,
            cancellationToken);
        if (note is not null)
        {
            // Route the selected note through the same gateway so the
            // existing environment checks, deduplication, playback gate,
            // and retry queue remain authoritative.
            await PublishAsync(
                DurableNotification.LocalNote(note, ambientRequest.AnimationKey),
                bypassSuppression: false,
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Mutates the pet state machine and plays its animation under the same
    /// gate <see cref="PetPresentationCoordinator"/> uses for explicit
    /// one-shots, so the two never interleave on the shared state machine.
    /// The notification sink call happens after releasing that gate: it has
    /// nothing to do with pet state and must not block an explicit user
    /// action behind a possibly-slow toast call.
    /// </summary>
    private async Task<bool> PresentAsync(DurableNotification item, CancellationToken cancellationToken)
    {
        await _petGate.WaitAsync(cancellationToken);
        PetPresentation presentation;
        var succeeded = true;
        try
        {
            var petEvent = ToPetEvent(item);
            presentation = _pet.Handle(petEvent);
            if (item.Kind == PresentationItemKind.LocalNote)
            {
                presentation = presentation with
                {
                    BubbleTitle = item.Title,
                    BubbleBody = item.Body,
                };
            }
            succeeded &= await ObserveAsync(
                () => _playAsync(presentation, _options(), cancellationToken),
                "presentation-playback");
        }
        finally
        {
            if (item.Kind == PresentationItemKind.LocalNote)
            {
                _pet.Handle(new PetEvent.PresentationAcknowledged());
                _pet.Handle(new PetEvent.AmbientDismissed(
                    item.AnimationKey ?? throw new InvalidOperationException(
                        "A local note presentation has no animation key.")));
            }
            _petGate.Release();
        }

        succeeded &= await ObserveAsync(
            () => ShowNotificationAsync(item, cancellationToken),
            "presentation-notification");
        return succeeded;
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
            PresentationItemKind.LocalNote => new PetEvent.AmbientRequested(
                item.AnimationKey ?? throw new InvalidOperationException(
                    "A local note presentation has no animation key.")),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unsupported presentation item kind."),
    };

    private SuppressionSnapshot CaptureEnvironment(DateTimeOffset now)
    {
        try
        {
            // Live re-sample: the pushed _fullscreen flag is reconciled with a
            // current read on every decision so PublishAsync/TickAsync never
            // act on a stale poll. Any read failure is fail-closed (hidden).
            var liveFullscreen = ReadFullscreenFailClosed();
            bool sessionLocked;
            lock (_gate)
            {
                sessionLocked = _sessionLocked;
                if (liveFullscreen != _fullscreen)
                {
                    _fullscreen = liveFullscreen;
                }
            }

            var paused = PausePolicy.IsSuppressed(_pauseState(), now, liveFullscreen);
            var focusActive = _pet.Current.State == PetState.Focus;
            return new SuppressionSnapshot(_isQuietHours(), liveFullscreen, paused, sessionLocked, focusActive);
        }
        catch (Exception exception)
        {
            // Single fail-closed policy: any environment fault suppresses.
            Trace.TraceError("Dudu presentation environment read failed: {0}", exception);
            return new SuppressionSnapshot(NowQuiet: true, Fullscreen: true, Paused: true, SessionLocked: true, FocusActive: true);
        }
    }

    private bool ReadFullscreenFailClosed()
    {
        try
        {
            return _isFullscreenNow();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu fullscreen re-sample failed: {0}", exception);
            return true;
        }
    }

    private static bool IsSuppressed(SuppressionSnapshot snapshot) =>
        snapshot.NowQuiet || snapshot.Fullscreen || snapshot.Paused
        || snapshot.SessionLocked || snapshot.FocusActive;

    private static async Task<bool> ObserveAsync(Func<Task> operation, string name)
    {
        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu {0} failed: {1}", name, exception);
            return false;
        }
    }

    private readonly record struct SuppressionSnapshot(
        bool NowQuiet,
        bool Fullscreen,
        bool Paused,
        bool SessionLocked,
        bool FocusActive);
}
