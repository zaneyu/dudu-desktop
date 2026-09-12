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
    private readonly TrayIconService? _tray;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _locked;
    private bool _suspended;
    private bool _fullscreenHidden;
    private bool _disposed;

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
        Action? openHome = null)
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
    }

    public async Task OnSessionLockedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _locked = true;
            _overlay.Hide();
        }
        finally { _gate.Release(); }
    }

    public async Task OnSessionUnlockedAsync(CancellationToken cancellationToken = default)
    {
        await ResumeAndMaybeWelcomeAsync(cancellationToken, clearLock: true);
    }

    public async Task OnSuspendAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _suspended = true;
            _overlay.Hide();
        }
        finally { _gate.Release(); }
    }

    public Task OnResumeAsync(CancellationToken cancellationToken = default) =>
        ResumeAndMaybeWelcomeAsync(cancellationToken, clearLock: false);

    public async Task OnHotkeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (CanShow(_isFullscreen()))
            {
                _openHome?.Invoke();
                _overlay.Show();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OnDisplayChangedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _overlay.RestorePlacement();
        }
        finally { _gate.Release(); }
    }

    public async Task OnFullscreenChangedAsync(
        bool fullscreen,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_preferences.HidePetDuringFullscreen) return;
            if (fullscreen)
            {
                _fullscreenHidden = true;
                _overlay.Hide();
            }
            else if (_fullscreenHidden)
            {
                _fullscreenHidden = false;
                _overlay.RestorePlacement();
                if (CanShow(false))
                {
                    _overlay.Show();
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OnTaskbarCreatedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _tray?.Recreate();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _overlay.Hide();
            _tray?.Dispose();
        }
        finally { _gate.Release(); }

        await _host.StopAsync();
        await _overlay.DisposeAsync();
        _gate.Dispose();
    }

    private async Task ResumeAndMaybeWelcomeAsync(
        CancellationToken cancellationToken,
        bool clearLock)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await _host.ResumeAsync(cancellationToken);
            if (clearLock)
            {
                _locked = false;
                _suspended = false;
            }
            else
            {
                _suspended = false;
            }

            var fullscreen = _isFullscreen();
            var suppressed = PausePolicy.IsSuppressed(_pauseState(), _clock(), fullscreen);
            if (!_locked && !_suspended && !fullscreen && !_isQuietHours() && !suppressed)
            {
                _pet.Handle(new PetEvent.WelcomeBackRequested());
            }

            if (!_fullscreenHidden && CanShow(fullscreen))
            {
                _overlay.Show();
            }
        }
        finally { _gate.Release(); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AppLifecycleCoordinator));
    }

    private bool CanShow(bool fullscreen)
    {
        if (_locked || _suspended || (fullscreen && _preferences.HidePetDuringFullscreen))
        {
            return false;
        }

        return !_isQuietHours()
            && !PausePolicy.IsSuppressed(_pauseState(), _clock(), fullscreen);
    }
}

public sealed class OverlayLifecycleAdapter(OverlayWindowHost host) : IOverlayLifecycle
{
    public bool IsVisible => host.IsVisible;
    public void Show() => host.Show();
    public void Hide() => host.Hide();
    public void RestorePlacement() => host.RestorePlacement();
    public ValueTask DisposeAsync() => host.DisposeAsync();
}
