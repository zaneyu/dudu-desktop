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
using Microsoft.Data.Sqlite;

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

    /// <summary>Keys discarded (<see cref="DiscardHeldAsync"/> /
    /// <see cref="DiscardHeldByKindAsync"/>) while still present in
    /// <see cref="_presentingIds"/> -- i.e. a presentation for that exact key
    /// was in flight at the moment it was discarded. <see cref="RequeueHeldAsync"/>
    /// and <see cref="TickAsync"/>'s decline path consult this so a
    /// presentation that then FAILS drops the item and deletes its row
    /// instead of requeuing and re-persisting something already discarded
    /// (e.g. a deleted note's text, or a reminder completed from the
    /// Reminders page). Cleared once that presentation ends, win or lose, so
    /// a later item that happens to reuse the same key is unaffected.</summary>
    private readonly HashSet<string> _discardedWhilePresentingIds = new(StringComparer.Ordinal);

    /// <summary>Which held-presentation repository operation kinds ("load",
    /// "persist", "remove"), each paired with the failing exception's type
    /// name and (for a <see cref="SqliteException"/>) its error code, have
    /// already had a failure reported this process, so
    /// <see cref="ReportHeldFailureOnce"/> reports each distinct failure at
    /// most once instead of on every reminder tick. Keying on kind alone
    /// would let one already-reported failure mode (e.g. a transient
    /// SQLITE_BUSY) permanently swallow a later, unrelated one (e.g. a
    /// corrupt database) under the same kind.</summary>
    private readonly HashSet<(string Kind, string ExceptionType, int SqliteErrorCode)> _reportedHeldFailureKinds = new();
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
        IHeldPresentationRepository? heldPresentations = null,
        bool initialUserHidden = false)
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
        // Finding B: production must start user-hidden until the lifecycle
        // coordinator pushes the first real value. Its own ctor also seeds
        // one (AppLifecycleCoordinator's constructor calls
        // _presentationEnvironment?.SetUserVisible(_userVisible)), but in
        // production that seed is a no-op: the coordinator is constructed
        // before this gateway exists, so the delegating sink's closure over
        // the (still-null) gateway variable silently drops it. The startup
        // reminder tick's direct call into the reminder engine (which still
        // runs even though its own reconcile and release are deferred, see
        // AppHost.RunReminderTickAsync) can publish a due reminder before
        // reconcile has run even once -- with the default "visible", that
        // publish would be evaluated against a pet this gateway wrongly
        // believes is on screen, animating it into a window that is not
        // shown yet and deleting its row on that "successful" presentation.
        // Tests and the Windows harness, which never push a value at all,
        // keep their prior behavior (default false) unless they opt in.
        _userHidden = initialUserHidden;
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
    /// Discards a pending unsolicited item -- from the in-memory queue, its
    /// toasted-while-held marker, and its persisted row -- without ever
    /// presenting it. Used when the event it represents is separately
    /// resolved before this gateway released it: completing a reminder from
    /// the Reminders page does not go through <see cref="PresentAsync"/> at
    /// all (it advances the reminder directly), so a copy that was queued or
    /// held for later would otherwise still surface here on a later tick or
    /// the next app launch even though it was already handled. A no-op (but
    /// still safe, since Remove/RemoveHeldAsync are themselves no-ops for a
    /// key that is not present) when the item was never queued or held.
    /// Not meant for an item currently in <see cref="_presentingIds"/> — this
    /// does not touch that set, so a concurrent presentation in flight for
    /// the same key is left alone.
    /// </summary>
    public async Task DiscardHeldAsync(
        PresentationItemKind kind,
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            // Finding F: callers invoke this after their own delete/consume
            // has already committed (e.g. LoveNotesViewModel deletes the
            // note, then tells this gateway to drop its held copy) -- a
            // blank id never had anything held for it anyway, so throwing
            // here would turn an already-successful user action into a
            // visible error for no reason.
            return;
        }

        var key = $"{kind}:{id}";
        lock (_gate)
        {
            // Finding 7: run under the same _gate as DiscardHeldByKindAsync's
            // own RemoveAllOfKind call, not after releasing it -- an item
            // published in the window between an unlocked Remove and this
            // lock would otherwise survive the discard entirely.
            _policy.Remove(key);
            if (_presentingIds.Contains(key))
            {
                // Finding E: a presentation for this exact key is in flight
                // right now. The queue/row removal below still runs (the row
                // must not outlive this discard), but if that in-flight
                // presentation then FAILS, the ordinary retry path would
                // otherwise requeue and re-persist the very item being
                // discarded here -- resurrecting deleted note text or a
                // reminder already completed elsewhere. RequeueHeldAsync and
                // TickAsync's decline path both consult this instead.
                //
                // Finding 9: leave _toastedWhileHeldIds alone while that
                // presentation is still in flight -- PresentAsync's own
                // `alreadyToasted` read races this discard, and clearing the
                // marker here unconditionally let it see alreadyToasted =
                // false and show a Windows toast for a reminder she just
                // completed elsewhere. PresentAsync now also consults
                // _discardedWhilePresentingIds directly to skip that toast.
                _discardedWhilePresentingIds.Add(key);
            }
            else
            {
                _toastedWhileHeldIds.Remove(key);
            }
        }

        await RemoveHeldAsync(key, cancellationToken);
    }

    /// <summary>
    /// Same as <see cref="DiscardHeldAsync"/> but discards every pending item
    /// of a given kind at once, for a cleanup that is not about one id — e.g.
    /// forgetting a broken pairing deletes every unopened remote note
    /// locally in one pass, not one message id at a time. A no-op when
    /// nothing of that kind is queued or held.
    /// </summary>
    public async Task DiscardHeldByKindAsync(
        PresentationItemKind kind,
        CancellationToken cancellationToken = default)
    {
        // Finding E: RemoveAllOfKind only ever sees PresentationPolicy's
        // in-memory queue -- an item of this kind currently being presented
        // has already been dequeued into _presentingIds and would otherwise
        // be missed entirely by a kind-wide discard.
        var presentingPrefix = $"{kind}:";
        List<string>? presentingKeysOfKind = null;
        IReadOnlyList<string> removedKeys;
        lock (_gate)
        {
            // Finding 14: RemoveAllOfKind must run under the same _gate as
            // PublishAsync's Enqueue call, not before it -- calling it ahead
            // of the lock let a remote note published in the window between
            // the removal and this lock survive a kind-wide discard entirely
            // (e.g. forgetting a broken pairing).
            removedKeys = _policy.RemoveAllOfKind(kind);
            foreach (var key in removedKeys)
            {
                _toastedWhileHeldIds.Remove(key);
            }

            foreach (var presentingKey in _presentingIds)
            {
                if (!presentingKey.StartsWith(presentingPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                _discardedWhilePresentingIds.Add(presentingKey);
                (presentingKeysOfKind ??= []).Add(presentingKey);
            }
        }

        foreach (var key in removedKeys)
        {
            await RemoveHeldAsync(key, cancellationToken);
        }

        if (presentingKeysOfKind is not null)
        {
            foreach (var key in presentingKeysOfKind)
            {
                await RemoveHeldAsync(key, cancellationToken);
            }
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
                bool enqueued;
                lock (_gate)
                {
                    enqueued = _policy.Enqueue(notification);
                    if (enqueued && record.Toasted)
                    {
                        _toastedWhileHeldIds.Add(notification.Key);
                    }
                }

                // Enqueue returns false for a duplicate kind+id already sitting
                // in the in-memory queue (e.g. two persisted rows collided on
                // the same key) or an Ambient item that should never have been
                // persisted. Either way this row will never be delivered from
                // here, so leaving it on disk would just reload and fail to
                // enqueue it again next launch, and marking it toasted would
                // orphan that marker forever since nothing downstream will ever
                // consume it for a key that was never actually queued.
                if (!enqueued)
                {
                    await RemoveHeldAsync(record.Key, cancellationToken);
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
        var shouldPresentNow = false;
        var toastNow = false;
        var queuedForHold = false;
        var alreadyQueued = false;
        lock (_gate)
        {
            if (_presentingIds.Contains(item.Key))
            {
                return;
            }

            var environment = EnvironmentFor(item, CaptureEnvironment(now));
            if (_policy.IsQueued(item))
            {
                // Finding 12: an earlier occurrence of this same recurring
                // item (same Kind:Id key) is already held. Silently
                // dropping every later occurrence used to deny her the
                // Windows toast too, for as long as the hold lasted (up to
                // 7 days, across restarts) -- only the pet animation needs
                // to wait for the original held item to release, not the
                // notification for a new occurrence arriving in the
                // meantime. Quiet-hours (and every other suppressor)
                // coalescing multiple occurrences into the single held row
                // is unchanged: this only fires when hiding Dudu is the
                // sole reason anything is held, exactly like toastNow below
                // (no second queue entry is added either way).
                alreadyQueued = true;
                toastNow = environment.UserHidden
                    && (bypassSuppression || !IsSuppressedExcludingUserHidden(environment));
                if (toastNow)
                {
                    // Same marker the sibling toastNow branch below sets:
                    // without it, the single held row's eventual release
                    // would show a second Windows toast for what is really
                    // just one still-held reminder, since PresentAsync's own
                    // alreadyToasted check would otherwise read false.
                    _toastedWhileHeldIds.Add(item.Key);
                }
            }
            else
            {
                // Finding 11: bypassSuppression alone used to let a
                // DeliverImmediately reminder animate straight into a
                // hidden or not-yet-shown overlay (startup with
                // initialUserHidden, or she hid Dudu from the tray) --
                // bypass must still respect UserHidden for presenting now.
                // Its toast (below, alongside the ordinary held-and-hidden
                // toast) still fires immediately either way.
                shouldPresentNow = (bypassSuppression && !environment.UserHidden) || !IsSuppressed(environment);
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
                    toastNow = queuedForHold && environment.UserHidden
                        && (bypassSuppression || !IsSuppressedExcludingUserHidden(environment));
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
        }

        if (alreadyQueued)
        {
            if (toastNow)
            {
                var toastShown = await ObserveAsync(
                    () => ShowNotificationAsync(item, cancellationToken),
                    "presentation-notification");

                if (toastShown)
                {
                    // Finding 6: the in-memory _toastedWhileHeldIds marker
                    // set above is not enough on its own -- a restart before
                    // the held row is ever released would reload it as
                    // untoasted and show this same toast a second time.
                    // Persist it the same way PresentAsync's own
                    // toastShown-and-not-succeeded path does (LocalNote has
                    // no durable row to mark, and no real toast either).
                    if (item.Kind != PresentationItemKind.LocalNote)
                    {
                        await MarkToastedAsync(item.Key, cancellationToken);
                    }
                }
                else
                {
                    // The toast itself failed -- undo the marker set above
                    // so a retry (the row is still held and will be
                    // released normally) is free to show it again instead
                    // of being permanently skipped as "already toasted".
                    lock (_gate)
                    {
                        _toastedWhileHeldIds.Remove(item.Key);
                    }
                }
            }

            return;
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
                await RequeueHeldAsync(item, now, cancellationToken, callerOwnsPresentingEntry: true);
            }
        }
        catch
        {
            await RequeueHeldAsync(item, now, cancellationToken, callerOwnsPresentingEntry: true);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _presentingIds.Remove(item.Key);
                // Finding E: RequeueHeldAsync above already clears this on
                // the failure path; also clear it here so a *succeeded*
                // presentation for a key that was discarded mid-flight does
                // not leave the marker behind to misfire against some later,
                // unrelated item recurring under the same key.
                _discardedWhilePresentingIds.Remove(item.Key);
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

            // Any other released item is excluded from toPresent because its
            // key is already in _presentingIds -- a concurrent caller (e.g.
            // an immediate PublishAsync, or another in-flight tick) is
            // presenting it right now. Decide() already dequeued it from
            // PresentationPolicy, so put it straight back rather than
            // dropping it: it is reconsidered on a later tick once that
            // concurrent presentation finishes and clears _presentingIds.
            foreach (var item in decision.ToPresent)
            {
                if (toPresent is not null && item.Key == toPresent.Key)
                {
                    continue;
                }

                _policy.Requeue(item);
            }
        }

        // A purge (expired while held) takes the item out of
        // PresentationPolicy's in-memory queue for good, so its persisted
        // row, if any, is removed immediately. toPresent's row is
        // deliberately left in place here too: it is only deleted below once
        // PresentAsync actually succeeds, so a crash partway through
        // presenting it (e.g. waiting on the pet gate, or during the
        // animation) leaves the row for the next launch to reload instead of
        // losing the item silently. A released-but-presenting-elsewhere item
        // was just requeued above (not removed): its row must stay too, for
        // the same reason -- a failed concurrent presentation may requeue-
        // and-repersist it and needs the row's original QueuedUtc to still
        // be meaningful, and if that concurrent attempt crashes instead, the
        // row is still there to reload on the next launch.
        foreach (var purgedKey in purgedKeys)
        {
            await RemoveHeldAsync(purgedKey, cancellationToken);
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
                    // in LoadHeldItemsAsync. Finding E: unless it was
                    // discarded while this exact attempt was in flight, in
                    // which case it must be dropped and deleted instead.
                    await RequeueOrDropDiscardedAsync(toPresent, cancellationToken);
                }
            }
            catch
            {
                await RequeueOrDropDiscardedAsync(toPresent, cancellationToken);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _presentingIds.Remove(toPresent.Key);
                    _discardedWhilePresentingIds.Remove(toPresent.Key);
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

            if (item.IsRoutine && IsSuppressed(EnvironmentFor(item, CaptureEnvironment(now))))
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
                () => PlayWithTimeoutAsync(presentation, cancellationToken),
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
            // Fire-and-forget: this runs on the 30 s tick, after the pet
            // gate above is already released, so nothing below depends on
            // the cue's outcome (succeeded was fixed by the earlier
            // presentation-playback observe and is not touched by audio).
            // Awaiting it here would make ShowNotificationAsync below wait
            // up to the cue's own bound for no reason. ObserveAudioAsync
            // still observes and reports the cue's exceptions on its own.
            _ = ObserveAudioAsync(() => _playAudioAsync(audioCue, cancellationToken));
        }

        bool alreadyToasted;
        bool discardedWhilePresenting;
        lock (_gate)
        {
            alreadyToasted = _toastedWhileHeldIds.Contains(item.Key);
            // Finding 9: this exact attempt was discarded (DiscardHeldAsync
            // / DiscardHeldByKindAsync) while it was still in flight -- the
            // reminder/note was already resolved elsewhere. Showing its
            // Windows toast now would surface a notification for something
            // she just completed or deleted.
            discardedWhilePresenting = _discardedWhilePresentingIds.Contains(item.Key);
        }

        if (!alreadyToasted && !discardedWhilePresenting)
        {
            // A failed Windows toast does not veto `succeeded`: by this point
            // it already reflects whether the pet animation actually played
            // (set above at the _playAsync call), and both PublishAsync and
            // TickAsync requeue-and-replay the whole item from the top when
            // this method returns false. Letting a toast-only failure flip an
            // already-successful animation to "failed" would show that
            // animation a second time later just because the separate,
            // best-effort OS notification did not land. The failure is still
            // reported inside ObserveAsync either way -- it just no longer
            // changes the return value.
            var toastShown = await ObserveAsync(
                () => ShowNotificationAsync(item, cancellationToken),
                "presentation-notification");

            // The animation can still have already failed by this point
            // (succeeded is false): without recording that the toast
            // happened, a retry -- whether the immediate re-present a few
            // lines up in PublishAsync's catch, or a later 30 s tick -- would
            // find alreadyToasted still false and show the exact same toast
            // again, forever, since a failing animation never lets the item
            // leave the queue. Marking it here makes the requeue (and
            // whatever persists the row afterward) see toasted=true, the same
            // as the userHidden-while-held path already does. Only latched
            // for a failed presentation: a succeeded one is done for good, so
            // leaving the marker set would just orphan it for a future item
            // recurring under the same key, exactly like the purge cleanup
            // above guards against.
            if (toastShown && !succeeded)
            {
                lock (_gate)
                {
                    _toastedWhileHeldIds.Add(item.Key);
                }

                if (item.Kind != PresentationItemKind.LocalNote)
                {
                    // Finding D: the marker above is memory-only. PersistHeldAsync
                    // already writes toasted=true into a *new* row (via
                    // RequeueHeldAsync, PublishAsync's own failure path), but
                    // TickAsync's decline path deliberately does not re-persist
                    // an already-held item's row (to preserve QueuedUtc) -- so
                    // without this, that row's toasted column stays false even
                    // though _toastedWhileHeldIds already remembers it in
                    // memory. A restart before the retry finally succeeds would
                    // then reload the row as untoasted and show the Windows
                    // toast a second time. A no-op when the row does not exist
                    // yet -- the fresh persist above already writes the correct
                    // flag. Skipped for LocalNote: ShowNotificationAsync shows
                    // no real toast for that kind (see the switch below), so
                    // toastShown is trivially true for it regardless of
                    // whether anything was actually shown.
                    await MarkToastedAsync(item.Key, cancellationToken);
                }
            }
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
            // Re-sampled on every decision so PublishAsync/TickAsync never
            // act on a stale poll -- but only when the constructor was given
            // a real isFullscreenNow callback; production does not pass one,
            // so this just reads back the last value SetFullscreen pushed
            // (see the isFullscreenNow default above), which starts out
            // false/unset until the events sink pushes a real reading during
            // WindowsCompanionBootstrap, after this host's own startup tick
            // has already run once. Any read failure is fail-closed (hidden).
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

    /// <summary>The environment as it applies to <paramref name="item"/>: an
    /// item that <see cref="DurableNotification.IgnoresQuietHours"/> (the
    /// bedtime routine) is not held by quiet hours; every other suppressor
    /// still applies to it.</summary>
    private static SuppressionSnapshot EnvironmentFor(DurableNotification item, SuppressionSnapshot snapshot) =>
        item.IgnoresQuietHours ? snapshot with { NowQuiet = false } : snapshot;

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

    private async Task PlayWithTimeoutAsync(PetPresentation presentation, CancellationToken cancellationToken)
    {
        // Loop animations (idle/focus) never complete on their own; without a
        // bound the petGate is held forever and later notes never present.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _playAsync(presentation, _options(), linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timed out on a loop/long clip: treat as shown, release the gate.
        }
    }

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
    private async Task PersistHeldAsync(
        DurableNotification item,
        DateTimeOffset queuedUtc,
        CancellationToken cancellationToken,
        bool callerOwnsPresentingEntry = false)
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
        // dequeue this row (moving it into _presentingIds/toPresent) before
        // this write lands, orphaning it (and duplicating the popup on the
        // next restart, which would reload and re-present something already
        // shown). Re-checking membership right after the write, and deleting
        // if it lost that race, closes the window.
        //
        // But a dequeue for *presentation* is not the same as a dequeue for
        // good: while toPresent is being presented it is tracked in
        // _presentingIds, not _policy, so IsQueued alone reads false for it.
        // Deleting the row in that window loses the item for real if the
        // presentation then fails: TickAsync's failure path calls
        // _policy.Requeue(toPresent) without re-persisting (deliberately, to
        // preserve QueuedUtc), relying on this row still being on disk to
        // requeue *from*. Only delete when the item is gone from both --
        // queued nowhere and not being presented either.
        //
        // Finding 7: when this call came from PublishAsync's own failed-
        // immediate-present retry (RequeueHeldAsync with
        // callerOwnsPresentingEntry: true), item.Key is still in
        // _presentingIds for the whole call -- it is PublishAsync's own
        // bookkeeping entry, not yet removed (that happens in its `finally`,
        // after this call returns), so it says nothing about whether anyone
        // else still cares. Counting it there made this guard unable to ever
        // fire for that path: a concurrent DiscardHeldAsync (e.g. the
        // reminder was completed from the Reminders page while this retry
        // was mid-flight) could remove the item from the queue for good, yet
        // the self-reference alone would keep "still relevant" true forever,
        // leaving a stale row to reload and re-present on the next launch.
        // Only _presentingIds entries owned by someone else still count.
        bool stillRelevant;
        lock (_gate)
        {
            stillRelevant = _policy.IsQueued(item)
                || (!callerOwnsPresentingEntry && _presentingIds.Contains(item.Key));
        }

        if (!stillRelevant)
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

    /// <summary>Finding D: persists the toasted-while-held marker for a row
    /// that may already exist on disk, independent of a full
    /// <see cref="PersistHeldAsync"/> upsert (which would reset QueuedUtc).
    /// No-op when no repository was supplied, or when the row does not exist
    /// yet -- the eventual first persist already writes the correct
    /// flag.</summary>
    private Task MarkToastedAsync(string key, CancellationToken cancellationToken)
    {
        if (_heldPresentations is null)
        {
            return Task.CompletedTask;
        }

        return ObserveHeldAsync(() => _heldPresentations.MarkToastedAsync(key, cancellationToken), "mark-toasted");
    }

    /// <summary>Returns a failed/cancelled immediate-presentation attempt
    /// (<see cref="PublishAsync"/> only -- TickAsync requeues a held item's
    /// failed retry itself, without touching its already-persisted row) to
    /// PresentationPolicy's durable queue and, only when it actually
    /// re-entered the queue, persists it for the first time with
    /// <paramref name="queuedUtc"/> as its QueuedUtc. <paramref
    /// name="callerOwnsPresentingEntry"/> must be true when the caller (this
    /// is only ever <see cref="PublishAsync"/>) still holds its own
    /// <c>_presentingIds</c> entry for this key while this call runs -- see
    /// the Finding 7 note in <see cref="PersistHeldAsync"/>.</summary>
    private async Task RequeueHeldAsync(
        DurableNotification item,
        DateTimeOffset queuedUtc,
        CancellationToken cancellationToken,
        bool callerOwnsPresentingEntry = false)
    {
        // Finding E: this exact attempt was discarded (DiscardHeldAsync /
        // DiscardHeldByKindAsync) while it was still in flight -- e.g. the
        // reminder was completed from the Reminders page, or the note was
        // deleted, while PublishAsync's immediate-present attempt for it was
        // still running. Requeuing and re-persisting it now, the ordinary
        // failure path below, would resurrect something the user already
        // got rid of. Drop it for good instead.
        bool discardedWhilePresenting;
        lock (_gate)
        {
            discardedWhilePresenting = _discardedWhilePresentingIds.Remove(item.Key);
            if (discardedWhilePresenting)
            {
                // Finding 10: PresentAsync's failure path can re-add this
                // key to _toastedWhileHeldIds (see the "toastShown &&
                // !succeeded" branch there) after it lost the race with a
                // discard that landed mid-presentation. Left behind, that
                // stale marker orphans the key -- for a recurring reminder,
                // silently swallowing tomorrow's Windows toast even though
                // today's item was dropped for good just above.
                _toastedWhileHeldIds.Remove(item.Key);
            }
        }

        if (discardedWhilePresenting)
        {
            _policy.Remove(item.Key);
            await RemoveHeldAsync(item.Key, cancellationToken);
            return;
        }

        if (_policy.Requeue(item))
        {
            await PersistHeldAsync(item, queuedUtc, cancellationToken, callerOwnsPresentingEntry);
        }
    }

    /// <summary>Finding E: TickAsync's own requeue-without-repersist path for
    /// a released item that was declined or whose presentation threw. Drops
    /// and deletes the row instead of requeuing when <paramref name="item"/>
    /// was discarded (<see cref="DiscardHeldAsync"/> /
    /// <see cref="DiscardHeldByKindAsync"/>) while this exact attempt was in
    /// flight -- see <see cref="_discardedWhilePresentingIds"/>.</summary>
    private async Task RequeueOrDropDiscardedAsync(DurableNotification item, CancellationToken cancellationToken)
    {
        bool discardedWhilePresenting;
        lock (_gate)
        {
            discardedWhilePresenting = _discardedWhilePresentingIds.Remove(item.Key);
            if (discardedWhilePresenting)
            {
                // Finding 10: see the matching comment in RequeueHeldAsync --
                // without this, a stale toasted-while-held marker left by
                // PresentAsync's failure path orphans the key and silently
                // swallows a later occurrence's Windows toast.
                _toastedWhileHeldIds.Remove(item.Key);
            }
        }

        if (discardedWhilePresenting)
        {
            await RemoveHeldAsync(item.Key, cancellationToken);
            return;
        }

        _policy.Requeue(item);
    }

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
    /// distinct (<paramref name="kind"/> ("load", "persist", "remove", or
    /// "mark-toasted"), exception type, Sqlite error code) combination for
    /// this process,
    /// instead of on every occurrence -- so a repeatedly-reported transient
    /// failure (e.g. SQLITE_BUSY) cannot latch out a later, different
    /// failure under the same kind (e.g. SQLITE_CORRUPT).</summary>
    private void ReportHeldFailureOnce(string kind, Exception exception)
    {
        var sqliteErrorCode = exception is SqliteException sqliteException
            ? sqliteException.SqliteErrorCode
            : 0;
        var key = (kind, exception.GetType().FullName ?? exception.GetType().Name, sqliteErrorCode);
        lock (_gate)
        {
            if (!_reportedHeldFailureKinds.Add(key))
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
