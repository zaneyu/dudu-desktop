using System.Diagnostics;
using Dudu.App.Overlay;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Hosting;

public interface IAppHostLifecycle
{
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IOverlayLifecycle : IAsyncDisposable
{
    bool IsVisible { get; }
    void Show();
    void Hide();
    void RestorePlacement();
}

public sealed class AppLifecycleCoordinator : IAsyncDisposable
{
    private readonly IAppHostLifecycle _host;
    private readonly IOverlayLifecycle _overlay;
    private readonly PetStateMachine _pet;
    private Preferences _preferences;
    private readonly Func<PauseState> _pauseState;
    private readonly Func<bool> _isQuietHours;
    private readonly Func<bool> _isFullscreen;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task>? _openHome;
    private readonly Action<Exception>? _diagnostic;
    private readonly IAppHostErrorReporter? _errorReporter;
    private readonly Func<PetEvent, string, CancellationToken, Task>? _presentOneShotAsync;
    private readonly IPresentationEnvironmentSink? _presentationEnvironment;
    private readonly TrayIconService? _tray;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _visibilityGate = new(1, 1);
    private readonly object _disposeSync = new();
    private bool _locked;
    private bool _suspended;
    private bool _fullscreenHidden;
    private bool _userVisible;
    private bool _disposed;
    private Task? _disposeTask;

    public AppLifecycleCoordinator(
        IAppHostLifecycle host,
        IOverlayLifecycle overlay,
        PetStateMachine pet,
        Preferences preferences,
        Func<PauseState>? pauseState = null,
        Func<bool>? isQuietHours = null,
        Func<bool>? isFullscreen = null,
        Func<DateTimeOffset>? clock = null,
        TrayIconService? tray = null,
        Func<CancellationToken, Task>? openHome = null,
        Action<Exception>? diagnostic = null,
        bool? initialUserVisible = null,
        IAppHostErrorReporter? errorReporter = null,
        Func<PetEvent, string, CancellationToken, Task>? presentOneShotAsync = null,
        IPresentationEnvironmentSink? presentationEnvironment = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _pauseState = pauseState ?? (() => PauseState.None);
        _isQuietHours = isQuietHours ?? (() => false);
        _isFullscreen = isFullscreen ?? (() => false);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tray = tray;
        _openHome = openHome;
        _diagnostic = diagnostic;
        _errorReporter = errorReporter;
        _userVisible = initialUserVisible ?? overlay.IsVisible;
        _presentOneShotAsync = presentOneShotAsync;
        _presentationEnvironment = presentationEnvironment;
        // Sync the sink with whatever visibility this instance started at,
        // the same way SetSessionLocked/SetFullscreen are seeded elsewhere —
        // otherwise a coordinator constructed already-hidden would leave
        // PresentationCoordinator believing the pet is visible until the
        // next explicit show/hide call.
        _presentationEnvironment?.SetUserVisible(_userVisible);
    }

    public Preferences CurrentPreferences => Volatile.Read(ref _preferences);

    public async Task UpdatePreferencesAsync(
        Preferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        bool reconcileFullscreen;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            // H2: OnFullscreenChangedAsync only fires on a fullscreen EDGE —
            // production wires it to the OS fullscreen-transition event, which
            // never fires again just because a setting changed. So if this
            // preference update turns "hide during fullscreen" off while she
            // is still hidden from an earlier fullscreen session, nothing
            // else will ever ask the overlay to come back. Detect that
            // specific true->false edge here and re-run the same
            // reconciliation OnFullscreenChangedAsync already knows how to do
            // (it already treats "setting off but still hidden" as a restore
            // case), once the gate is released below.
            reconcileFullscreen = _preferences.HidePetDuringFullscreen && !preferences.HidePetDuringFullscreen;
            Volatile.Write(ref _preferences, preferences);
        }
        finally { _gate.Release(); }

        if (reconcileFullscreen)
        {
            await OnFullscreenChangedAsync(
                TryReadFullscreen("preferences-fullscreen-reconcile"),
                cancellationToken);
        }
    }

    public async Task OnSessionLockedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _locked = true;
        }
        finally { _gate.Release(); }

        await InvokeVisualSafelyAsync(_overlay.Hide, "session-lock-hide", cancellationToken);
    }

    public Task OnSessionUnlockedAsync(CancellationToken cancellationToken = default) =>
        ResumeAndMaybeWelcomeAsync(cancellationToken, clearLock: true);

    public async Task OnSuspendAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _suspended = true;
        }
        finally { _gate.Release(); }

        await InvokeVisualSafelyAsync(_overlay.Hide, "suspend-hide", cancellationToken);
    }

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAndMaybeWelcomeAsync(cancellationToken, clearLock: false);

    public async Task OnHotkeyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var fullscreen = TryReadFullscreen("hotkey-fullscreen");
        // The hotkey is an explicit user gesture (also used for second
        // launch/activation, see WindowsCompanionRuntime.ActivateAsync):
        // quiet hours suppress proactive presentation only, never a request
        // she made herself.
        if (!TryCanShow(fullscreen, snapshot.Locked, snapshot.Suspended, "hotkey-gate", ignoreQuietHours: true))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_locked != snapshot.Locked
                || _suspended != snapshot.Suspended
                || _fullscreenHidden != snapshot.FullscreenHidden)
            {
                return;
            }

            _userVisible = true;
            _presentationEnvironment?.SetUserVisible(true);
        }
        finally { _gate.Release(); }

        if (_openHome is not null)
        {
            await _openHome(cancellationToken);
        }
        await InvokeVisualSafelyAsync(_overlay.Show, "hotkey-show", cancellationToken);
    }

    public async Task OnUserShowOrHideAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        // Keyed on the overlay's real visibility, not just the desired
        // _userVisible flag: quiet hours (TryCanShow) or a pause-state hide
        // can leave _userVisible true while the overlay itself never came
        // up or was hidden again. Branching on desired state alone made the
        // first tray click a no-op in exactly that case -- it took the HIDE
        // branch against an overlay that was already hidden.
        if (snapshot.UserVisible && _overlay.IsVisible)
        {
            await SetUserVisibleAsync(false, cancellationToken);
            return;
        }

        // Tray "show dudu" is an explicit user gesture: quiet hours suppress
        // proactive presentation only, never a request she made herself.
        await EnsureUserVisibleAsync(cancellationToken, explicitUserGesture: true);
    }

    public Task SetUserVisibleAsync(
        bool visible,
        CancellationToken cancellationToken = default) =>
        visible
            ? EnsureUserVisibleAsync(cancellationToken)
            : HideForUserAsync(cancellationToken);

    private async Task EnsureUserVisibleAsync(
        CancellationToken cancellationToken,
        bool explicitUserGesture = false)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        await _visibilityGate.WaitAsync(cancellationToken);
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                // This is desired state, not permission to bypass a gate.
                // Keeping it true lets fullscreen/pause/session transitions
                // restore the pet after suppression ends.
                _userVisible = true;
                _presentationEnvironment?.SetUserVisible(true);

                var fullscreen = TryReadFullscreen("show-fullscreen");
                if (snapshot.FullscreenHidden
                    || _fullscreenHidden
                    || fullscreen
                    || _locked
                    || _suspended
                    || !TryCanShow(fullscreen, _locked, _suspended, "show-gate", ignoreQuietHours: explicitUserGesture))
                {
                    return;
                }

                // Keep the lifecycle gate held until the show request is
                // issued. OnFullscreenChangedAsync updates the same state
                // under this gate before it can hide the request again.
                InvokeSafely(_overlay.Show, "user-show");
            }
            finally { _gate.Release(); }
        }
        finally { _visibilityGate.Release(); }
    }

    private async Task HideForUserAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _userVisible = false;
            _presentationEnvironment?.SetUserVisible(false);
        }
        finally { _gate.Release(); }

        await InvokeVisualSafelyAsync(_overlay.Hide, "user-hide", cancellationToken);
    }

    public async Task OnDisplayChangedAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAsync(cancellationToken);
        await InvokeVisualSafelyAsync(_overlay.RestorePlacement, "display-placement", cancellationToken);
    }

    public async Task OnFullscreenChangedAsync(
        bool fullscreen,
        CancellationToken cancellationToken = default)
    {
        bool hide;
        bool restore;
        bool restoreVisible;
        GateSnapshot snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (CurrentPreferences.HidePetDuringFullscreen && fullscreen)
            {
                if (_fullscreenHidden) return;
                _fullscreenHidden = true;
                hide = true;
                restore = false;
                restoreVisible = false;
                snapshot = new GateSnapshot(_locked, _suspended, true, _userVisible);
            }
            else
            {
                // Either she isn't in fullscreen anymore, or "hide during
                // fullscreen" was turned off while she was still hidden from
                // an earlier fullscreen session. Either way she must not
                // stay hidden forever waiting for a fullscreen-exit
                // notification that the now-disabled setting no longer cares
                // about.
                if (!_fullscreenHidden) return;
                restoreVisible = _userVisible;
                _fullscreenHidden = false;
                hide = false;
                restore = true;
                snapshot = new GateSnapshot(_locked, _suspended, false, _userVisible);
            }
        }
        finally { _gate.Release(); }

        if (hide)
        {
            await InvokeVisualSafelyAsync(_overlay.Hide, "fullscreen-hide", cancellationToken);
            return;
        }

        if (!restore) return;
        await _visibilityGate.WaitAsync(cancellationToken);
        try
        {
            InvokeSafely(_overlay.RestorePlacement, "fullscreen-placement");
            if (!restoreVisible
                || !TryCanShow(
                    TryReadFullscreen("fullscreen-restore-fullscreen"),
                    snapshot.Locked,
                    snapshot.Suspended,
                    "fullscreen-restore-gate"))
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                if (_fullscreenHidden
                    || _locked != snapshot.Locked
                    || _suspended != snapshot.Suspended)
                {
                    return;
                }
            }
            finally { _gate.Release(); }

            var latest = await CaptureAsync(cancellationToken);
            if (!latest.UserVisible
                || latest.FullscreenHidden
                || !TryCanShow(
                    TryReadFullscreen("fullscreen-restore-final-fullscreen"),
                    latest.Locked,
                    latest.Suspended,
                    "fullscreen-restore-final-gate"))
            {
                return;
            }

            InvokeSafely(_overlay.Show, "fullscreen-restore-show");
        }
        finally { _visibilityGate.Release(); }
    }

    public async Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAsync(cancellationToken);
        if (_tray is null) return;
        try { await _tray.RecreateAsync(cancellationToken); }
        catch (Exception exception) { ReportFailure("taskbar-tray-recreate", exception); }
    }

    public async Task OnPauseStateChangedAsync(CancellationToken cancellationToken = default)
    {
        await _visibilityGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await CaptureAsync(cancellationToken);
            var fullscreen = TryReadFullscreen("pause-fullscreen");
            var canShow = TryCanShow(
                fullscreen,
                snapshot.Locked,
                snapshot.Suspended,
                "pause-state-gate");
            if (snapshot.UserVisible && canShow && !snapshot.FullscreenHidden)
            {
                InvokeSafely(_overlay.Show, "pause-state-show");
            }
            else if (!canShow)
            {
                InvokeSafely(_overlay.Hide, "pause-state-hide");
            }
        }
        finally { _visibilityGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
        }
        finally { _gate.Release(); }

        await InvokeVisualSafelyAsync(_overlay.Hide, "shutdown-hide", CancellationToken.None);
        if (_tray is not null)
        {
            try { await _tray.DisposeAsync(); }
            catch (Exception exception) { ReportFailure("shutdown-tray", exception); }
        }
        try
        {
            await _host.StopAsync();
        }
        catch (Exception exception)
        {
            // Logging only: the shutdown failure still propagates to the
            // caller exactly as before, but the operation name and exception
            // type are now captured for field diagnostics.
            ReportFailure("shutdown-host", exception);
            throw;
        }
        try
        {
            await _overlay.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportFailure("shutdown-overlay", exception);
            throw;
        }
    }

    private async Task<GateSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return new GateSnapshot(_locked, _suspended, _fullscreenHidden, _userVisible);
        }
        finally { _gate.Release(); }
    }

    private async Task ResumeAndMaybeWelcomeAsync(
        CancellationToken cancellationToken,
        bool clearLock)
    {
        await _host.ResumeAsync(cancellationToken);

        GateSnapshot snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (clearLock)
            {
                _locked = false;
                _suspended = false;
            }
            else
            {
                _suspended = false;
            }

            snapshot = new GateSnapshot(_locked, _suspended, _fullscreenHidden, _userVisible);
        }
        finally { _gate.Release(); }

        var fullscreen = TryReadFullscreen("resume-fullscreen");
        var canShow = TryCanShow(fullscreen, snapshot.Locked, snapshot.Suspended, "resume-gate");
        var welcome = canShow && !fullscreen && !snapshot.FullscreenHidden;
        var show = canShow && !snapshot.FullscreenHidden && snapshot.UserVisible;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_locked != snapshot.Locked
                || _suspended != snapshot.Suspended
                || _fullscreenHidden != snapshot.FullscreenHidden)
            {
                welcome = false;
                show = false;
            }
        }
        finally { _gate.Release(); }

        // Show before presenting the welcome-back greeting so the one-shot
        // animation (when routed through PresentOneShotAsync) is not played
        // and dismissed against a still-hidden overlay.
        if (show)
        {
            await InvokeVisualSafelyAsync(_overlay.Show, "resume-show", cancellationToken);
        }

        if (welcome)
        {
            await PresentWelcomeBackAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Requests the welcome-back pose through the same one-shot presentation
    /// path an explicit user action uses (e.g. <c>TasksFocusViewModel.EndFocusAsync</c>),
    /// so the request is acknowledged and dismissed once shown instead of
    /// latching <see cref="PetState.WelcomeBack"/> forever — nothing else
    /// ever raises <see cref="PetEvent.WelcomeBackDismissed"/>. Falls back to
    /// a raw <see cref="PetStateMachine.Handle"/> call when no one-shot
    /// delegate is composed (e.g. in tests that only assert the pending pose).
    /// </summary>
    private async Task PresentWelcomeBackAsync(CancellationToken cancellationToken)
    {
        if (_presentOneShotAsync is null)
        {
            // M2: no one-shot delegate composed (e.g. a test exercising only
            // the raw state machine). Immediately complete the request the
            // same way the real one-shot path would, instead of leaving
            // WelcomeBack latched with nothing left to ever dismiss it.
            var requested = new PetEvent.WelcomeBackRequested();
            _pet.Handle(requested);
            _pet.Handle(PetEvent.CompletionForOneShot(requested, "welcome-back"));
            return;
        }

        try
        {
            await _presentOneShotAsync(new PetEvent.WelcomeBackRequested(), "welcome-back", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure("resume-welcome-back", exception);
        }
    }

    /// <summary>
    /// Quiet hours suppress proactive presentation (welcome-back, fullscreen/
    /// pause restore) only. An explicit user gesture -- tray "show dudu",
    /// the global hotkey, or a second launch/activation -- always shows the
    /// pet, so those callers pass <paramref name="ignoreQuietHours"/>. Lock,
    /// suspend, fullscreen, and the pause policy are not gestures the user
    /// is actively making right now and still gate every caller.
    /// </summary>
    private bool TryCanShow(
        bool fullscreen,
        bool locked,
        bool suspended,
        string operation,
        bool ignoreQuietHours = false)
    {
        try
        {
            if (locked || suspended || (fullscreen && CurrentPreferences.HidePetDuringFullscreen))
            {
                return false;
            }

            return (ignoreQuietHours || !_isQuietHours())
                && !PausePolicy.IsSuppressed(_pauseState(), _clock(), fullscreen);
        }
        catch (Exception exception)
        {
            ReportFailure(operation, exception);
            return false;
        }
    }

    private bool TryReadFullscreen(string operation)
    {
        try { return _isFullscreen(); }
        catch (Exception exception)
        {
            ReportFailure(operation, exception);
            return true;
        }
    }

    private void InvokeSafely(Action? callback, string operation)
    {
        if (callback is null) return;
        try { callback(); }
        catch (Exception exception) { ReportFailure(operation, exception); }
    }

    private async Task InvokeVisualSafelyAsync(
        Action callback,
        string operation,
        CancellationToken cancellationToken)
    {
        await _visibilityGate.WaitAsync(cancellationToken);
        try { InvokeSafely(callback, operation); }
        finally { _visibilityGate.Release(); }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        // The reporter (when composed) carries the operation name plus the
        // exception to the shared AppHost sink so a future file sink captures
        // it. The legacy Action<Exception> diagnostic is preserved for
        // existing callers; the bare Trace fallback logs the operation name
        // plus the exception type/HResult only — never a message or body that
        // could carry private content.
        if (_errorReporter is not null)
        {
            try
            {
                _errorReporter.Report(operation, exception);
                return;
            }
            catch { }
        }

        if (_diagnostic is not null)
        {
            try { _diagnostic(exception); }
            catch { }
            return;
        }

        Trace.TraceError(
            "Dudu lifecycle operation '{0}' failed: {1} (0x{2:X8})",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AppLifecycleCoordinator));
    }

    private readonly record struct GateSnapshot(
        bool Locked,
        bool Suspended,
        bool FullscreenHidden,
        bool UserVisible);
}

public sealed class OverlayLifecycleAdapter(OverlayWindowHost host) : IOverlayLifecycle
{
    public bool IsVisible => host.IsVisible;
    public void Show() => host.Show();
    public void Hide() => host.Hide();
    public void RestorePlacement() => host.RestorePlacement();
    public ValueTask DisposeAsync() => host.DisposeAsync();
}
