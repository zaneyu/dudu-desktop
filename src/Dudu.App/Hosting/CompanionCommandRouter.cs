using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Abstractions;
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
    private PauseState _current = PauseState.None;

    public PauseState Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(PauseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate) _current = state;
    }

    public PauseState GetEffective(DateTimeOffset now)
    {
        lock (_gate)
        {
            _current = PausePolicy.ExpireIfNeeded(_current, now);
            return _current;
        }
    }
}

public sealed class CompanionCommandRouter
{
    private readonly AppLifecycleCoordinator _lifecycle;
    private readonly IPauseStateStore _pause;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action _openSettings;
    private readonly Action _exit;

    public CompanionCommandRouter(
        AppLifecycleCoordinator lifecycle,
        IPauseStateStore pause,
        Action openSettings,
        Action exit,
        Func<DateTimeOffset>? clock = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _pause = pause ?? throw new ArgumentNullException(nameof(pause));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Handle(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ShowOrHide:
                Run(_lifecycle.OnUserShowOrHideAsync(), "tray-show-hide");
                break;
            case TrayCommand.PauseOneHour:
                SetPause(PausePolicy.ForOneHour(_clock()), "tray-pause-one-hour");
                break;
            case TrayCommand.PauseUntilTomorrowAtSeven:
                SetPause(
                    PausePolicy.UntilTomorrowAtSeven(_clock()),
                    "tray-pause-tomorrow");
                break;
            case TrayCommand.PauseUntilFullscreenEnds:
                SetPause(
                    new PauseState(PauseMode.UntilFullscreenEnds, null),
                    "tray-pause-fullscreen");
                break;
            case TrayCommand.PauseIndefinitelyOrResume:
                SetPause(
                    _pause.GetEffective(_clock()).Mode == PauseMode.Indefinite
                        ? PauseState.None
                        : new PauseState(PauseMode.Indefinite, null),
                    "tray-pause-toggle");
                break;
            case TrayCommand.OpenSettings:
                Invoke(_openSettings, "tray-open-settings");
                break;
            case TrayCommand.Exit:
                Invoke(_exit, "tray-exit");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private void SetPause(PauseState state, string operation)
    {
        _pause.Set(state);
        Run(_lifecycle.OnPauseStateChangedAsync(), operation);
    }

    private static void Run(Task task, string operation)
    {
        _ = Observe(task, operation);
    }

    private static void Invoke(Action callback, string operation)
    {
        try { callback(); }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError(
                "Dudu command '{0}' failed: {1}",
                operation,
                exception);
        }
    }

    private static async Task Observe(Task task, string operation)
    {
        try { await task; }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError(
                "Dudu command '{0}' failed: {1}",
                operation,
                exception);
        }
    }
}

public sealed class StartupSettingsService
{
    private readonly StartupRegistrationService _startup;
    private readonly IPreferencesRepository _repository;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Preferences _preferences;
    private bool _needsReconciliation;
    private string? _reconciliationError;
    private bool? _reconciliationDesiredState;

    public StartupSettingsService(
        StartupRegistrationService startup,
        IPreferencesRepository repository,
        Preferences preferences)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public Preferences Current => _preferences;

    public bool NeedsReconciliation => _needsReconciliation;

    public string? ReconciliationError => _reconciliationError;

    public bool DesiredLaunchAtSignIn => _reconciliationDesiredState ?? _preferences.LaunchAtSignIn;

    public void Adopt(Preferences preferences, bool preserveReconciliation = true)
    {
        var hadActiveReconciliation = _needsReconciliation;
        var desiredState = _reconciliationDesiredState;
        var reconciliationError = _reconciliationError;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        if (preserveReconciliation && hadActiveReconciliation)
        {
            _reconciliationDesiredState = desiredState;
            _needsReconciliation = true;
            _reconciliationError = reconciliationError;
        }
        else
        {
            _reconciliationDesiredState = null;
            _needsReconciliation = false;
            _reconciliationError = null;
        }
    }

    public async Task SetLaunchAtSignInAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var updated = _preferences with { LaunchAtSignIn = enabled };
            await _repository.SaveAsync(updated, cancellationToken);
            _preferences = updated;
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
            await ReconcileCoreAsync(_reconciliationDesiredState ?? _preferences.LaunchAtSignIn, cancellationToken);
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
            _reconciliationError = "Startup registration needs another try.";
            throw new InvalidOperationException(_reconciliationError, exception);
        }
    }
}
