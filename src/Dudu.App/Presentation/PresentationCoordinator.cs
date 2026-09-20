using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Audio;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;

namespace Dudu.App.Presentation;

/// <summary>
/// Pushes session-lock, fullscreen, and user-visibility transitions into a
/// <see cref="PresentationCoordinator"/> without exposing
/// <c>AppLifecycleCoordinator</c>'s private state. Pause is read directly
/// from the pause store on each decision instead of being pushed.
/// </summary>
public interface IPresentationEnvironmentSink
{
    void SetSessionLocked(bool locked);

    void SetFullscreen(bool fullscreen);

    /// <summary>
    /// Pushed whenever <c>AppLifecycleCoordinator</c>'s own tracked
    /// visibility (tray toggle, hotkey show, or explicit hide) changes, so
    /// pet presentations can be held while she has hidden Dudu and released
    /// on un-hide, the same way they already are for fullscreen/pause.
    /// </summary>
    void SetUserVisible(bool visible);
}

/// <summary>The publish surface used by durable unsolicited-event sinks.</summary>
public interface IUnsolicitedPresentationGateway
{
    Task PublishAsync(
        DurableNotification item,
        bool bypassSuppression,
        CancellationToken cancellationToken = default);
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
    IPresentationEnvironmentSink,
    IUnsolicitedPresentationGateway
{
    private readonly PresentationPolicy _policy;
    private readonly INotificationService _notifications;
    private readonly PetStateMachine _pet;
    private readonly Func<PetPresentation, AnimationOptions, CancellationToken, Task> _playAsync;
    private readonly Func<AudioCueEvent, CancellationToken, Task>? _playAudioAsync;
    private readonly Func<AnimationOptions> _options;
    private readonly Func<bool> _isQuietHours;
    private readonly Func<PauseState> _pauseState;
    private readonly SemaphoreSlim _petGate;
    private readonly AmbientScheduler? _ambientScheduler;
    private readonly LocalNoteSelector? _localNoteSelector;
    private readonly IReadOnlyList<string>? _availableStickerKeys;
    private readonly object _gate = new();
    private readonly HashSet<string> _presentingIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toastedWhileHeldIds = new(StringComparer.Ordinal);

    /// <summary>Which held-presentation repository operation kinds ("load",
    /// "persist", "remove") have already had a failure reported this
    /// process, so <see cref="ReportHeldFailureOnce"/> reports each kind at
    /// most once instead of on every reminder tick.</summary>
    private readonly HashSet<string> _reportedHeldFailureKinds = new(StringComparer.Ordinal);
    private readonly Func<bool> _isFullscreenNow;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly IHeldPresentationRepository? _heldPresentations;
    private bool _sessionLocked;
    private bool _fullscreen;
    private bool _userHidden;

    /// <summary>
    /// A held row this old on load is presumed abandoned (its retry has been
    /// failing since before the last restart) rather than durably retried
    /// forever. See <see cref="RequeueHeldAsync"/> for why a failed retry no
    /// longer resets <c>QueuedUtc</c>, which is what makes this cutoff mean
    /// actual age instead of time-since-last-attempt.
    /// </summary>
    private static readonly TimeSpan MaxHeldAge = TimeSpan.FromDays(7);

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
        Func<bool>? isFullscreenNow = null,
        Func<DateTimeOffset>? utcNow = null,
        IAppHostErrorReporter? errorReporter = null,
        Func<AudioCueEvent, CancellationToken, Task>? playAudioAsync = null,
        IReadOnlyList<string>? availableStickerKeys = null,
        IHeldPresentationRepository? heldPresentations = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _playAsync = playAsync ?? throw new ArgumentNullException(nameof(playAsync));
        _playAudioAsync = playAudioAsync;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isQuietHours = isQuietHours ?? throw new ArgumentNullException(nameof(isQuietHours));
        _pauseState = pauseState ?? throw new ArgumentNullException(nameof(pauseState));
        _petGate = petGate ?? throw new ArgumentNullException(nameof(petGate));
        _ambientScheduler = ambientScheduler;
        _localNoteSelector = localNoteSelector;
        _availableStickerKeys = availableStickerKeys;
        _isFullscreenNow = isFullscreenNow ?? (() => { lock (_gate) return _fullscreen; });
        _errorReporter = errorReporter;
        _heldPresentations = heldPresentations;
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

    public bool IsSessionLocked
    {
        get { lock (_gate) return _sessionLocked; }
    }

    public bool IsFullscreen
    {
        get { lock (_gate) return _fullscreen; }
    }

    public void SetFullscreen(bool fullscreen)
    {
        lock (_gate)
        {
            _fullscreen = fullscreen;
        }
    }

    public void SetUserVisible(bool visible)
    {
        lock (_gate)
        {
            _userHidden = !visible;
        }
    }

    /// <summary>
    /// Registers Windows app notifications. A registration failure is
    /// swallowed here: it must never throw out of startup, and durable
    /// events still reach the user through the pet bubble fallback.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await LoadHeldItemsAsync(cancellationToken);

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
            ReportFailure("presentation-tick", exception);
        }
    }

    /// <summary>
    /// Repopulates PresentationPolicy's in-memory queue from
    /// <see cref="IHeldPresentationRepository"/> so an item held back by
    /// quiet hours/fullscreen/lock/pause at the moment the app last quit or
    /// crashed is not lost. No-op when no repository was supplied. A row that
    /// has already expired, or is older than <see cref="MaxHeldAge"/>, is
    /// dropped (deleted, not enqueued) rather than surfaced on this launch;
    /// a row this build no longer recognizes (kind, or a required field
    /// missing) is reported and dropped the same way rather than wedging
    /// startup. A row marked toasted re-seeds the in-memory
    /// toasted-while-held marker so its Windows toast is not shown a second
    /// time once released.
    /// </summary>
    private async Task LoadHeldItemsAsync(CancellationToken cancellationToken)
    {
        if (_heldPresentations is null)
        {
            return;
        }

        IReadOnlyList<HeldPresentation> held;
        try
        {
            held = await _heldPresentations.ListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportHeldFailureOnce("load", exception);
            return;
        }

        var now = _utcNow();
        var cutoff = now - MaxHeldAge;
        foreach (var record in held)
        {
            try
            {
                if ((record.ExpiresUtc is { } expiry && now >= expiry) || record.QueuedUtc < cutoff)
                {
                    await RemoveHeldAsync(record.Key, cancellationToken);
                    continue;
                }

                var notification = ToDurableNotification(record);
                lock (_gate)
                {
                    _policy.Enqueue(notification);
                    if (record.Toasted)
                    {
                        _toastedWhileHeldIds.Add(notification.Key);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportHeldFailureOnce("load", exception);
                await RemoveHeldAsync(record.Key, cancellationToken);
            }
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
        var now = _utcNow();
        if (item.IsExpired(now))
        {
            return;
        }

        bypassSuppression &= !item.IsRoutine;
        bool shouldPresentNow;
        var toastNow = false;
        var queuedForHold = false;
        lock (_gate)
        {
            if (_presentingIds.Contains(item.Key) || _policy.IsQueued(item))
            {
                return;
            }

            var environment = CaptureEnvironment(now);
            shouldPresentNow = bypassSuppression || !IsSuppressed(environment);
            if (!shouldPresentNow)
            {
                queuedForHold = _policy.Enqueue(item);
                // A Windows toast is independent of whether the overlay is
                // on screen, unlike the pet animation this item's queueing
                // already holds back — so when the *only* reason it is held
                // is that she has hidden Dudu from the tray, the toast
                // still fires now instead of waiting for un-hide together
                // with the animation. Marked so the eventual real
                // PresentAsync (once released) does not show the same
                // toast a second time.
                toastNow = queuedForHold && environment.UserHidden && !IsSuppressedExcludingUserHidden(environment);
                if (toastNow)
                {
                    _toastedWhileHeldIds.Add(item.Key);
                }
            }
            else
            {
                _presentingIds.Add(item.Key);
            }
        }

        if (queuedForHold)
        {
            // Held back by quiet hours/fullscreen/lock/pause: persist it so
            // it survives a quit or crash while still queued. No-op when no
            // IHeldPresentationRepository was supplied.
            await PersistHeldAsync(item, now, cancellationToken);
        }

        if (!shouldPresentNow)
        {
            if (toastNow)
            {
                await ObserveAsync(
                    () => ShowNotificationAsync(item, cancellationToken),
                    "presentation-notification");
            }

            return;
        }

        try
        {
            if (!await PresentAsync(item, cancellationToken))
            {
                await RequeueHeldAsync(item, now, cancellationToken);
            }
        }
        catch
        {
            await RequeueHeldAsync(item, now, cancellationToken);
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
        var now = _utcNow();
        DurableNotification? toPresent;
        SuppressionSnapshot environment;
        IReadOnlyList<string> purgedKeys;
        IReadOnlyList<DurableNotification> released;
        lock (_gate)
        {
            environment = CaptureEnvironment(now);
            var decision = _policy.Decide(
                environment.NowQuiet,
                environment.Fullscreen,
                environment.Paused,
                environment.SessionLocked,
                environment.FocusActive,
                now,
                recordRelease: false,
                userHidden: environment.UserHidden);
            purgedKeys = decision.PurgedKeys;
            released = decision.ToPresent;

            // An item purged here was queued while held (and already toasted
            // for that hold) but expired before ever reaching PresentAsync,
            // so nothing downstream will ever clear its toasted-while-held
            // entry. Left alone it orphans that key — most concretely for a
            // routine reminder, which recurs daily under the same key, so a
            // stale entry from yesterday would silently swallow today's
            // Windows toast.
            foreach (var purgedKey in decision.PurgedKeys)
            {
                _toastedWhileHeldIds.Remove(purgedKey);
            }

            toPresent = decision.ToPresent.FirstOrDefault(item => !_presentingIds.Contains(item.Key));
            if (toPresent is not null)
            {
                _presentingIds.Add(toPresent.Key);
            }
        }

        // A purge (expired while held) takes the item out of
        // PresentationPolicy's in-memory queue for good, so its persisted
        // row, if any, is removed immediately. A release does too, but
        // toPresent's row is deliberately left in place here: it is only
        // deleted below once PresentAsync actually succeeds, so a crash
        // partway through presenting it (e.g. waiting on the pet gate, or
        // during the animation) leaves the row for the next launch to
        // reload instead of losing the item silently. Any other released
        // item is not going to be presented by this tick (defensively —
        // e.g. it raced with a concurrent PublishAsync and got excluded
        // from toPresent above) and is removed now like a purge, since
        // nothing below will act on it.
        foreach (var purgedKey in purgedKeys)
        {
            await RemoveHeldAsync(purgedKey, cancellationToken);
        }

        foreach (var item in released)
        {
            if (toPresent is not null && item.Key == toPresent.Key)
            {
                continue;
            }

            await RemoveHeldAsync(item.Key, cancellationToken);
        }

        if (toPresent is not null)
        {
            try
            {
                if (await PresentAsync(toPresent, cancellationToken))
                {
                    await RemoveHeldAsync(toPresent.Key, cancellationToken);
                }
                else
                {
                    // Declined rather than thrown (e.g. suppressed again by
                    // the time it ran): its row is still there from before
                    // this tick, so just put it back in the in-memory queue
                    // without re-persisting -- re-persisting here would
                    // reset QueuedUtc and defeat the abandoned-item cutoff
                    // in LoadHeldItemsAsync.
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
            || environment.FocusActive
            || environment.UserHidden)
        {
            return;
        }

        var ambient = _ambientScheduler.TryGetNextEvent(
            environment.Paused,
            environment.FocusActive,
            environment.Fullscreen,
            environment.SessionLocked,
            environment.NowQuiet,
            _availableStickerKeys);
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
        // Only true once the item's event has actually been handed to the
        // state machine below — the two early returns above (expired /
        // suppressed-routine) leave nothing latched, so the finally block
        // must not issue a Dismissed(id) for either: the id was never added
        // to the pending set, and dismissing it regardless can wipe out an
        // unrelated pending item that happens to share the id from an
        // earlier cycle.
        var latched = false;
        try
        {
            var now = _utcNow();
            if (item.IsExpired(now))
            {
                // Dropped for good (no requeue): clear its toasted-while-held
                // entry here too, for the same reason the policy's own purge
                // does in TickAsync — otherwise it orphans the key for a
                // future item recurring under it.
                lock (_gate)
                {
                    _toastedWhileHeldIds.Remove(item.Key);
                }

                return true;
            }

            if (item.IsRoutine && IsSuppressed(CaptureEnvironment(now)))
            {
                return false;
            }

            _policy.RecordImmediateRelease(now);
            var petEvent = ToPetEvent(item);
            presentation = _pet.Handle(petEvent);
            latched = true;
            if (item.Kind == PresentationItemKind.Reminder
                && presentation.State == PetState.Reminder
                && (item.Body is not null || item.AnimationKey is not null))
            {
                presentation = presentation with
                {
                    AnimationKey = item.AnimationKey ?? presentation.AnimationKey,
                    BubbleTitle = item.Title,
                    BubbleBody = item.Body ?? presentation.BubbleBody,
                };
            }
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
            else if (latched && succeeded && item.Kind is PresentationItemKind.Reminder or PresentationItemKind.RemoteNote)
            {
                // A reminder/note id otherwise sits in the state machine's
                // pending set forever (cleared only by an explicit Settings
                // dismiss/complete): Select() would keep ranking it above
                // ambient, welcome-back, and even a completed focus session
                // for the rest of the session. Acknowledging it here, the
                // same way a LocalNote is acknowledged above, lets the pet
                // return to its normal presentation once this specific item
                // has actually been shown; the coalesced card just shows one
                // fewer pending item if others remain. Gated on latched so
                // the two early-return paths above (item was never handed
                // to the state machine) never dismiss an id that was never
                // added, and on succeeded so a failed playback requeues
                // instead of vanishing.
                _pet.Handle(new PetEvent.Dismissed(item.Id));
            }
            _petGate.Release();
        }

        if (succeeded && _playAudioAsync is not null
            && AudioCueSelection.ForNotification(item) is { } audioCue)
        {
            await ObserveAudioAsync(() => _playAudioAsync(audioCue, cancellationToken));
        }

        bool alreadyToasted;
        lock (_gate)
        {
            alreadyToasted = _toastedWhileHeldIds.Contains(item.Key);
        }

        if (!alreadyToasted)
        {
            succeeded &= await ObserveAsync(
                () => ShowNotificationAsync(item, cancellationToken),
                "presentation-notification");
        }
        else if (succeeded)
        {
            // Only consume the marker once this presentation has actually
            // succeeded for good. A failed attempt is requeued by the
            // caller for a later retry, and that retry must still see
            // alreadyToasted so it does not show a second Windows toast for
            // an item that was already toasted once while held.
            lock (_gate)
            {
                _toastedWhileHeldIds.Remove(item.Key);
            }
        }

        return succeeded;
    }

    private async Task ObserveAudioAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("presentation-tick", exception);
        }
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
            bool userHidden;
            lock (_gate)
            {
                sessionLocked = _sessionLocked;
                userHidden = _userHidden;
                if (liveFullscreen != _fullscreen)
                {
                    _fullscreen = liveFullscreen;
                }
            }

            var paused = PausePolicy.IsSuppressed(_pauseState(), now, liveFullscreen);
            var focusActive = _pet.Current.State == PetState.Focus;
            return new SuppressionSnapshot(_isQuietHours(), liveFullscreen, paused, sessionLocked, focusActive, userHidden);
        }
        catch (Exception exception)
        {
            // Single fail-closed policy: any environment fault suppresses.
            ReportFailure("presentation-tick", exception);
            return new SuppressionSnapshot(NowQuiet: true, Fullscreen: true, Paused: true, SessionLocked: true, FocusActive: true, UserHidden: true);
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
            ReportFailure("presentation-tick", exception);
            return true;
        }
    }

    private static bool IsSuppressed(SuppressionSnapshot snapshot) =>
        snapshot.NowQuiet || snapshot.Fullscreen || snapshot.Paused
        || snapshot.SessionLocked || snapshot.FocusActive || snapshot.UserHidden;

    /// <summary>
    /// Same suppression check as <see cref="IsSuppressed"/> but leaving out
    /// <see cref="SuppressionSnapshot.UserHidden"/> — used to detect the case
    /// where the pet-hidden-from-tray state is the *only* thing holding an
    /// item back, so its toast (which has nothing to do with the on-screen
    /// overlay) can still fire immediately instead of waiting on un-hide.
    /// </summary>
    private static bool IsSuppressedExcludingUserHidden(SuppressionSnapshot snapshot) =>
        snapshot.NowQuiet || snapshot.Fullscreen || snapshot.Paused
        || snapshot.SessionLocked || snapshot.FocusActive;

    private async Task<bool> ObserveAsync(Func<Task> operation, string name)
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
            // Playback/notification failures are reported under the single
            // presentation-tick operation carrying the exception type only —
            // no note or reminder content is ever logged.
            ReportFailure("presentation-tick", exception);
            return false;
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        if (_errorReporter is not null)
        {
            try { _errorReporter.Report(operation, exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu {0} failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }

    /// <summary>No-op when no repository was supplied. A save failure is
    /// reported (throttled, see <see cref="ReportHeldFailureOnce"/>) and
    /// swallowed, matching every other secondary concern in this class (a
    /// toast, audio): it must never fail the presentation it is
    /// tracking.</summary>
    private async Task PersistHeldAsync(DurableNotification item, DateTimeOffset queuedUtc, CancellationToken cancellationToken)
    {
        if (_heldPresentations is null)
        {
            return;
        }

        bool toasted;
        lock (_gate)
        {
            toasted = _toastedWhileHeldIds.Contains(item.Key);
        }

        var record = new HeldPresentation(
            item.Key,
            item.Kind.ToString(),
            item.Id,
            item.Title,
            item.Body,
            item.AnimationKey,
            item.ExpiresUtc,
            queuedUtc,
            toasted);
        await ObserveHeldAsync(() => _heldPresentations.SaveAsync(record, cancellationToken), "persist");

        // H1: PublishAsync/RequeueHeldAsync enqueue the item into
        // PresentationPolicy under _gate, then persist it here afterward (a
        // DB call must not run under a lock) — so a concurrent TickAsync can
        // dequeue-and-delete this row before this write lands, orphaning it
        // (and duplicating the popup on the next restart, which would reload
        // and re-present something already shown). Re-checking membership
        // right after the write, and deleting if it lost that race, closes
        // the window.
        if (!_policy.IsQueued(item))
        {
            await RemoveHeldAsync(item.Key, cancellationToken);
        }
    }

    /// <summary>Deletes the persisted row for a key that just left
    /// PresentationPolicy's in-memory queue for good (released for
    /// presentation, purged as expired, or lost the H1 race in
    /// <see cref="PersistHeldAsync"/>). No-op when no repository was
    /// supplied.</summary>
    private Task RemoveHeldAsync(string key, CancellationToken cancellationToken)
    {
        if (_heldPresentations is null)
        {
            return Task.CompletedTask;
        }

        return ObserveHeldAsync(() => _heldPresentations.DeleteAsync(key, cancellationToken), "remove");
    }

    /// <summary>Returns a failed/cancelled immediate-presentation attempt
    /// (<see cref="PublishAsync"/> only -- TickAsync requeues a held item's
    /// failed retry itself, without touching its already-persisted row) to
    /// PresentationPolicy's durable queue and, only when it actually
    /// re-entered the queue, persists it for the first time with
    /// <paramref name="queuedUtc"/> as its QueuedUtc.</summary>
    private Task RequeueHeldAsync(DurableNotification item, DateTimeOffset queuedUtc, CancellationToken cancellationToken) =>
        _policy.Requeue(item) ? PersistHeldAsync(item, queuedUtc, cancellationToken) : Task.CompletedTask;

    /// <summary>Runs a held-presentation repository call, reporting a
    /// failure (throttled via <see cref="ReportHeldFailureOnce"/>) and
    /// swallowing it rather than letting it propagate. Kept separate from
    /// <see cref="ObserveAsync"/>'s other callers because this one runs on
    /// every reminder tick (as often as every 30 seconds): an unthrottled
    /// report would drown out anything else in the log/error reporter for as
    /// long as a transient DB problem lasts.</summary>
    private async Task<bool> ObserveHeldAsync(Func<Task> operation, string kind)
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
            ReportHeldFailureOnce(kind, exception);
            return false;
        }
    }

    /// <summary>Reports a held-presentation failure at most once per
    /// <paramref name="kind"/> ("load", "persist", or "remove") for this
    /// process, instead of on every occurrence.</summary>
    private void ReportHeldFailureOnce(string kind, Exception exception)
    {
        lock (_gate)
        {
            if (!_reportedHeldFailureKinds.Add(kind))
            {
                return;
            }
        }

        ReportFailure($"presentation-held-{kind}", exception);
    }

    /// <summary>Rebuilds the <see cref="DurableNotification"/> a
    /// <see cref="HeldPresentation"/> row was saved from. Throws for a row
    /// this build cannot make sense of (unrecognized kind, or a field its
    /// kind requires is missing) — the caller in <see cref="LoadHeldItemsAsync"/>
    /// treats that as corrupt and drops the row.</summary>
    private static DurableNotification ToDurableNotification(HeldPresentation record) => record.Kind switch
    {
        nameof(PresentationItemKind.RemoteNote) => DurableNotification.RemoteNote(record.Id),
        nameof(PresentationItemKind.Reminder) => DurableNotification.Reminder(
            record.Id,
            record.Title ?? throw new InvalidOperationException("A held reminder has no title."),
            record.Body,
            record.AnimationKey,
            record.ExpiresUtc),
        nameof(PresentationItemKind.LocalNote) => DurableNotification.LocalNote(
            new LocalLoveNote(
                record.Id,
                record.Body ?? throw new InvalidOperationException("A held local note has no text.")),
            record.AnimationKey ?? throw new InvalidOperationException("A held local note has no animation key.")),
        _ => throw new InvalidOperationException($"Unrecognized held presentation kind '{record.Kind}'."),
    };

    private readonly record struct SuppressionSnapshot(
        bool NowQuiet,
        bool Fullscreen,
        bool Paused,
        bool SessionLocked,
        bool FocusActive,
        bool UserHidden = false);
}
