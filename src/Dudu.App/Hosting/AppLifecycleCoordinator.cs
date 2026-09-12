using System.Diagnostics;
using Dudu.App.Overlay;
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
    private readonly Preferences _preferences;
    private readonly Func<PauseState> _pauseState;
    private readonly Func<bool> _isQuietHours;
    private readonly Func<bool> _isFullscreen;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action? _openHome;
    private readonly Action<Exception>? _diagnostic;
    private readonly TrayIconService? _tray;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeSync = new();
    private bool _locked;
    private bool _suspended;
    private bool _fullscreenHidden;
    private bool _userVisible;
    private bool _visibleBeforeFullscreen;
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
        Action? openHome = null,
        Action<Exception>? diagnostic = null,
        bool? initialUserVisible = null)
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
        _userVisible = initialUserVisible ?? overlay.IsVisible;
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

        InvokeSafely(_overlay.Hide, "session-lock-hide");
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

        InvokeSafely(_overlay.Hide, "suspend-hide");
    }

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAndMaybeWelcomeAsync(cancellationToken, clearLock: false);

    public async Task OnHotkeyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var fullscreen = TryReadFullscreen("hotkey-fullscreen");
        if (!TryCanShow(fullscreen, snapshot.Locked, snapshot.Suspended, "hotkey-gate"))
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
        }
        finally { _gate.Release(); }

        InvokeSafely(_openHome, "open-home");
        InvokeSafely(_overlay.Show, "hotkey-show");
    }

    public async Task OnUserShowOrHideAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        if (snapshot.UserVisible)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                _userVisible = false;
            }
            finally { _gate.Release(); }

            InvokeSafely(_overlay.Hide, "user-hide");
            return;
        }

        var fullscreen = TryReadFullscreen("show-fullscreen");
        if (!TryCanShow(fullscreen, snapshot.Locked, snapshot.Suspended, "show-gate"))
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
        }
        finally { _gate.Release(); }

        InvokeSafely(_overlay.Show, "user-show");
    }

    public async Task OnDisplayChangedAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAsync(cancellationToken);
        InvokeSafely(_overlay.RestorePlacement, "display-placement");
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
            if (!_preferences.HidePetDuringFullscreen) return;
            if (fullscreen)
            {
                if (_fullscreenHidden) return;
                _visibleBeforeFullscreen = _userVisible;
                _fullscreenHidden = true;
                hide = true;
                restore = false;
                restoreVisible = false;
                snapshot = new GateSnapshot(_locked, _suspended, true, _userVisible);
            }
            else
            {
                if (!_fullscreenHidden) return;
                restoreVisible = _visibleBeforeFullscreen;
                _visibleBeforeFullscreen = false;
                _fullscreenHidden = false;
                hide = false;
                restore = true;
                snapshot = new GateSnapshot(_locked, _suspended, false, _userVisible);
            }
        }
        finally { _gate.Release(); }

        if (hide)
        {
            InvokeSafely(_overlay.Hide, "fullscreen-hide");
            return;
        }

        if (!restore) return;
        InvokeSafely(_overlay.RestorePlacement, "fullscreen-placement");
        if (!restoreVisible
            || !TryCanShow(false, snapshot.Locked, snapshot.Suspended, "fullscreen-restore-gate"))
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

        InvokeSafely(_overlay.Show, "fullscreen-restore-show");
    }

    public async Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAsync(cancellationToken);
        try { _tray?.Recreate(); }
        catch (Exception exception) { ReportFailure("taskbar-tray-recreate", exception); }
    }

    public async Task OnPauseStateChangedAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var fullscreen = TryReadFullscreen("pause-fullscreen");
        var canShow = TryCanShow(fullscreen, snapshot.Locked, snapshot.Suspended, "pause-state-gate");
        if (snapshot.UserVisible && canShow && !snapshot.FullscreenHidden)
        {
            InvokeSafely(_overlay.Show, "pause-state-show");
        }
        else if (!canShow)
        {
            InvokeSafely(_overlay.Hide, "pause-state-hide");
        }
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

        InvokeSafely(_overlay.Hide, "shutdown-hide");
        try { _tray?.Dispose(); }
        catch (Exception exception) { ReportFailure("shutdown-tray", exception); }
        await _host.StopAsync();
        await _overlay.DisposeAsync();
        _gate.Dispose();
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

        if (welcome) _pet.Handle(new PetEvent.WelcomeBackRequested());
        if (show) InvokeSafely(_overlay.Show, "resume-show");
    }

    private bool TryCanShow(bool fullscreen, bool locked, bool suspended, string operation)
    {
        try
        {
            if (locked || suspended || (fullscreen && _preferences.HidePetDuringFullscreen))
            {
                return false;
            }

            return !_isQuietHours()
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

    private void ReportFailure(string operation, Exception exception)
    {
        if (_diagnostic is not null)
        {
            try { _diagnostic(exception); }
            catch { }
            return;
        }

        Trace.TraceError("Dudu lifecycle operation '{0}' failed: {1}", operation, exception);
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
