using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;

namespace Dudu.App.Hosting;

public interface IPauseStateStore
{
    PauseState Current { get; }
    PauseState GetEffective(DateTimeOffset now);
    void Set(PauseState state);
}

public sealed class PauseStateStore : IPauseStateStore
{
    private readonly object _gate = new();
    private readonly Func<bool>? _isFullscreen;
    private PauseState _current = PauseState.None;
    private bool _fullscreenSeenDuringPause;

    /// <param name="isFullscreen">
    /// Optional live fullscreen reading. When supplied, a
    /// <see cref="PauseMode.UntilFullscreenEnds"/> pause actually ends once
    /// a fullscreen session that began while it was active is over. Without
    /// it that pause never expired: Home kept saying Dudu was resting
    /// "till fullscreen done" and anything that treats every non-None pause
    /// as paused (audio cues) stayed muted until she resumed by hand.
    /// </param>
    public PauseStateStore(Func<bool>? isFullscreen = null)
    {
        _isFullscreen = isFullscreen;
    }

    public PauseState Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(PauseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _current = state;
            _fullscreenSeenDuringPause = false;
        }
    }

    public PauseState GetEffective(DateTimeOffset now)
    {
        PauseState observed;
        lock (_gate)
        {
            _current = PausePolicy.ExpireIfNeeded(_current, now);
            if (_current.Mode != PauseMode.UntilFullscreenEnds || _isFullscreen is null)
            {
                return _current;
            }

            observed = _current;
        }

        // Read outside the lock: the probe may take other components' locks.
        bool fullscreen;
        try
        {
            fullscreen = _isFullscreen();
        }
        catch
        {
            // Fail closed: an unreadable fullscreen state keeps the pause.
            fullscreen = true;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_current, observed))
            {
                return _current;
            }

            if (fullscreen)
            {
                _fullscreenSeenDuringPause = true;
            }
            else if (_fullscreenSeenDuringPause)
            {
                _current = PauseState.None;
                _fullscreenSeenDuringPause = false;
            }

            return _current;
        }
    }
}

public sealed class CompanionCommandRouter
{
    private readonly AppLifecycleCoordinator _lifecycle;
    private readonly IPauseStateStore _pause;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task> _openSettings;
    private readonly Func<CancellationToken, Task> _exit;

    public CompanionCommandRouter(
        AppLifecycleCoordinator lifecycle,
        IPauseStateStore pause,
        Func<CancellationToken, Task> openSettings,
        Func<CancellationToken, Task> exit,
        Func<DateTimeOffset>? clock = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _pause = pause ?? throw new ArgumentNullException(nameof(pause));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task HandleAsync(
        TrayCommand command,
        CancellationToken cancellationToken = default)
    {
        switch (command)
        {
            case TrayCommand.ShowOrHide:
                await _lifecycle.OnUserShowOrHideAsync(cancellationToken);
                break;
            case TrayCommand.PauseOneHour:
                await SetPauseAsync(PausePolicy.ForOneHour(_clock()), cancellationToken);
                break;
            case TrayCommand.PauseUntilTomorrowAtSeven:
                await SetPauseAsync(
                    PausePolicy.UntilTomorrowAtSeven(_clock()),
                    cancellationToken);
                break;
            case TrayCommand.PauseUntilFullscreenEnds:
                await SetPauseAsync(
                    new PauseState(PauseMode.UntilFullscreenEnds, null),
                    cancellationToken);
                break;
            case TrayCommand.PauseIndefinitelyOrResume:
                // "...or resume" must resume *any* active pause. Only an
                // indefinite pause used to resume here: after "pause for one
                // hour" the same item turned the timed pause into an
                // indefinite one instead, so resuming early took two clicks
                // through an item whose label promised one.
                await SetPauseAsync(
                    _pause.GetEffective(_clock()).Mode != PauseMode.None
                        ? PauseState.None
                        : new PauseState(PauseMode.Indefinite, null),
                    cancellationToken);
                break;
            case TrayCommand.OpenSettings:
                await _openSettings(cancellationToken);
                break;
            case TrayCommand.Exit:
                await _exit(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private async Task SetPauseAsync(PauseState state, CancellationToken cancellationToken)
    {
        _pause.Set(state);
        await _lifecycle.OnPauseStateChangedAsync(cancellationToken);
    }
}

public sealed class StartupSettingsService
{
    private readonly StartupRegistrationService _startup;
    private readonly PreferenceMutationCoordinator _preferenceMutations;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _needsReconciliation;
    private string? _reconciliationError;
    private bool? _reconciliationDesiredState;

    public StartupSettingsService(
        StartupRegistrationService startup,
        PreferenceMutationCoordinator preferenceMutations)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _preferenceMutations = preferenceMutations
            ?? throw new ArgumentNullException(nameof(preferenceMutations));
    }

    public Preferences Current => _preferenceMutations.Current;

    public PreferenceMutationCoordinator PreferenceMutations => _preferenceMutations;

    public bool NeedsReconciliation => _needsReconciliation;

    public string? ReconciliationError => _reconciliationError;

    public bool DesiredLaunchAtSignIn => _reconciliationDesiredState ?? Current.LaunchAtSignIn;

    /// <summary>The last applied state, as opposed to <see cref="Current"/>.LaunchAtSignIn
    /// which records what the user asked for even while that request is still unreconciled. A
    /// toggle that failed to apply must revert to this, not to the desired preference -- but on
    /// a packaged (MSIX) install the underlying registration has no confirmed reading yet until
    /// a write has actually succeeded (<see cref="StartupRegistrationService.IsEnabledKnown"/>),
    /// so until then this falls back to the desired/persisted value instead of confidently
    /// reporting "off" while Windows might still have the task registered.</summary>
    public bool ActualLaunchAtSignIn => _startup.IsEnabledKnown ? _startup.IsEnabled : DesiredLaunchAtSignIn;

    public async Task SetLaunchAtSignInAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var updated = await _preferenceMutations.UpdateAsync(
                current => current with { LaunchAtSignIn = enabled },
                cancellationToken);
            await ReconcileCoreAsync(updated.LaunchAtSignIn, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RetryStartupRegistrationAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await ReconcileCoreAsync(_reconciliationDesiredState ?? Current.LaunchAtSignIn, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Replaces any provisional reconciliation target with the
    /// preference snapshot that is authoritative after onboarding commits.</summary>
    public async Task ReconcileAuthoritativeAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _reconciliationDesiredState = null;
            await ReconcileCoreAsync(Current.LaunchAtSignIn, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReconcileExternalAsync(
        bool desiredLaunchAtSignIn,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await ReconcileCoreAsync(desiredLaunchAtSignIn, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ReconcileCoreAsync(
        bool desiredLaunchAtSignIn,
        CancellationToken cancellationToken)
    {
        _reconciliationDesiredState = desiredLaunchAtSignIn;
        try
        {
            await _startup.SetEnabledAsync(desiredLaunchAtSignIn, cancellationToken);
            _needsReconciliation = false;
            _reconciliationError = null;
            _reconciliationDesiredState = null;
        }
        catch (Exception exception)
        {
            _needsReconciliation = true;
            _reconciliationError = "aiyo startup registration needs another try";
            throw new InvalidOperationException(_reconciliationError, exception);
        }
    }
}
