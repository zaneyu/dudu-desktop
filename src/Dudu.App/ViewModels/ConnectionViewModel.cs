using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Abstractions;

namespace Dudu.App.ViewModels;

/// <summary>
/// Audit finding F3: a destructive Connection-page action awaiting confirmation. Mirrors
/// <see cref="PrivacyConfirmationAction"/>'s request-then-confirm pattern so every one-click
/// revoke/delete/forget button here gets the same guard rail.
/// </summary>
public enum ConnectionConfirmationAction
{
    None,
    RevokeSessions,
    DeleteRemoteDevice,
    ForgetPairing,
}

public sealed class ConnectionViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private PairingAvailability _availability = PairingAvailability.Offline;
    private PairingStatusReason _statusReason = PairingStatusReason.None;
    private string? _pairingCode;
    private DateTimeOffset? _codeExpiresUtc;
    private int _sessionCount;
    private ConnectionConfirmationAction _pendingConfirmation;

    public ConnectionViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        CreateCodeCommand = new AsyncRelayCommand((CancellationToken ct) => CreateCodeAsync(ct));
        RequestRevokeSessionsCommand = new RelayCommand(() => RequestConfirmation(ConnectionConfirmationAction.RevokeSessions));
        RequestDeleteRemoteDeviceCommand = new RelayCommand(() => RequestConfirmation(ConnectionConfirmationAction.DeleteRemoteDevice));
        RequestForgetPairingCommand = new RelayCommand(() => RequestConfirmation(ConnectionConfirmationAction.ForgetPairing));
        ConfirmCommand = new AsyncRelayCommand(
            () => ConfirmAsync(CancellationToken.None),
            () => PendingConfirmation != ConnectionConfirmationAction.None);
        CancelConfirmationCommand = new RelayCommand(CancelConfirmation);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand CreateCodeCommand { get; }
    public IRelayCommand RequestRevokeSessionsCommand { get; }
    public IRelayCommand RequestDeleteRemoteDeviceCommand { get; }
    public IRelayCommand RequestForgetPairingCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelConfirmationCommand { get; }
    public ObservableCollection<PairingSessionSummary> Sessions { get; } = [];
    public PairingAvailability Availability
    {
        get => _availability;
        private set
        {
            if (SetProperty(ref _availability, value))
            {
                OnPropertyChanged(nameof(IsPaired));
                OnPropertyChanged(nameof(AvailabilityText));
            }
        }
    }
    public PairingStatusReason StatusReason
    {
        get => _statusReason;
        private set
        {
            if (SetProperty(ref _statusReason, value)) OnPropertyChanged(nameof(AvailabilityText));
        }
    }
    public string? PairingCode
    {
        get => _pairingCode;
        private set
        {
            if (SetProperty(ref _pairingCode, value)) OnPropertyChanged(nameof(PairingCodeText));
        }
    }
    public DateTimeOffset? CodeExpiresUtc
    {
        get => _codeExpiresUtc;
        private set
        {
            if (SetProperty(ref _codeExpiresUtc, value)) OnPropertyChanged(nameof(CodeExpiryText));
        }
    }
    public int SessionCount
    {
        get => _sessionCount;
        private set
        {
            if (SetProperty(ref _sessionCount, value))
            {
                OnPropertyChanged(nameof(IsPaired));
                OnPropertyChanged(nameof(SessionCountText));
            }
        }
    }
    public bool IsPaired => Availability == PairingAvailability.Available && SessionCount > 0;
    public ConnectionConfirmationAction PendingConfirmation
    {
        get => _pendingConfirmation;
        private set
        {
            if (!SetProperty(ref _pendingConfirmation, value)) return;
            OnPropertyChanged(nameof(ConfirmationTitle));
            OnPropertyChanged(nameof(ConfirmationMessage));
            OnPropertyChanged(nameof(ConfirmationButtonText));
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }
    public string ConfirmationTitle => PendingConfirmation == ConnectionConfirmationAction.None
        ? "confirmation needed"
        : "confirm this action";
    public string ConfirmationMessage => PendingConfirmation switch
    {
        ConnectionConfirmationAction.RevokeSessions => "revoke all paired sender sessions now",
        ConnectionConfirmationAction.DeleteRemoteDevice => "delete remote device data cannot undo",
        // F2: distinct from DeleteRemoteDevice -- this never touches the relay, only the local
        // pairing and secrets, so it works even when the relay is unreachable or the secret store
        // is unreadable.
        // Review H3: short plain sentences instead of the page's usual cutesy shorthand -- this is
        // a destructive confirmation, and what she loses genuinely differs by case (this app
        // cannot know in advance which case applies, so both are stated), so clarity wins here.
        ConnectionConfirmationAction.ForgetPairing =>
            "Forgets pairing on this PC only. Unopened love notes usually stay safe and reappear "
            + "after you pair again. If your saved key turns out to be broken, forgetting also "
            + "deletes those unopened notes for good. Either way, your partner will need a new "
            + "pairing code from you afterward.",
        _ => "pick an action above to see effect",
    };
    public string ConfirmationButtonText => PendingConfirmation switch
    {
        ConnectionConfirmationAction.RevokeSessions => "confirm revoke",
        ConnectionConfirmationAction.DeleteRemoteDevice => "confirm delete",
        ConnectionConfirmationAction.ForgetPairing => "confirm forget pairing",
        _ => "confirm",
    };
    // A specific reason always wins over the coarse availability: "pairing offline dudu still
    // works here" is true but useless when the real answer is "you never set a relay up" (I9) or
    // "the relay is answering with something dudu cannot read" (C1).
    public string AvailabilityText => StatusReason switch
    {
        PairingStatusReason.RelayNotConfigured => "no relay yet notes stay local",
        PairingStatusReason.RelayProtocolError => "relay talking weird try again later",
        _ => Availability switch
        {
            PairingAvailability.Available => "can pair now",
            PairingAvailability.NeedsRepair => "pairing needs fixing before it works",
            _ => "pairing offline dudu still works here",
        },
    };
    public string PairingCodeText => string.IsNullOrWhiteSpace(PairingCode)
        ? "no code yet ah"
        : $"pairing code {PairingCode}";
    public string CodeExpiryText => CodeExpiresUtc is null
        ? "no code expiry yet"
        : $"code expires {CodeExpiresUtc.Value.ToLocalTime():g}";
    public string SessionCountText => SessionCount switch
    {
        0 => "no sender sessions yet ah",
        1 => "1 paired sender session",
        _ => $"{SessionCount} paired sender sessions",
    };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            var availability = await _context.Pairing.GetStateAsync(ct);
            var reason = _context.Pairing.StatusReason;
            List<PairingSessionSummary> sessions = new();
            try
            {
                foreach (var session in await _context.Pairing.ListSessionsAsync(ct)) sessions.Add(session);
            }
            catch (NotSupportedException)
            {
                // No per-session listing available; fall back to the aggregate count below.
            }

            var count = sessions.Count > 0
                ? sessions.Count
                : await _context.Pairing.GetSessionCountAsync(ct);
            await MutateAsync(() =>
            {
                Availability = availability;
                StatusReason = reason;
                Sessions.Clear();
                foreach (var session in sessions) Sessions.Add(session);
                SessionCount = count;
                OnPropertyChanged(nameof(IsPaired));
                // M3: the page is cached and Page_Loaded calls RefreshAsync on every visit, not
                // just the first one. A confirmation left pending from a previous visit (the user
                // requested an action, then navigated away without confirming or cancelling) must
                // not still be armed and shown as if freshly requested when they come back.
                PendingConfirmation = ConnectionConfirmationAction.None;
            }, ct);
        }, cancellationToken);
    }

    public Task CreateCodeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.CreateCodeAsync(cancellationToken);
            Availability = result.Availability;
            StatusReason = _context.Pairing.StatusReason;
            PairingCode = result.Code;
            CodeExpiresUtc = result.ExpiresUtc;
            if (result.Availability != PairingAvailability.Available ||
                string.IsNullOrWhiteSpace(result.Code) || result.ExpiresUtc is null)
            {
                throw new NotSupportedException("oh no pairing unavailable relay offline");
            }
        }, "yayyy code ready for 10 min");

    // F3: no longer bound directly to a button -- each is reached only through ConfirmAsync,
    // after RequestConfirmation put the page into the matching pending-confirmation state.
    private Task<bool> RevokeSessionsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.RevokeSessionsWithResultAsync(cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "cannot revoke sessions right now");
            await MutateAsync(() =>
            {
                Sessions.Clear();
                SessionCount = 0;
                OnPropertyChanged(nameof(IsPaired));
            }, cancellationToken);
        }, "done le sessions revoked");

    private Task<bool> DeleteRemoteDeviceAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.DeleteRemoteDeviceWithResultAsync(cancellationToken: cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "alala cant delete device data now");
            await MutateAsync(() =>
            {
                PairingCode = null;
                CodeExpiresUtc = null;
                Sessions.Clear();
                SessionCount = 0;
                Availability = PairingAvailability.Offline;
                OnPropertyChanged(nameof(IsPaired));
            }, cancellationToken);
        }, "can remote data deleted le");

    // F2's escape hatch: forgets this desktop's pairing on this machine only, without reading any
    // existing secret or contacting the relay, so it works even when GetStateAsync/RefreshAsync
    // already reported NeedsRepair (an unreadable secret) or Offline (relay unreachable) --
    // exactly the state that leaves DeleteRemoteDeviceAsync unable to help.
    private Task<bool> ForgetPairingAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _context.Pairing.ForgetPairingAsync(cancellationToken);
            // ForgetPairingLocallyAsync only wipes every local remote-note
            // envelope when the desktop's ECDH key turned out unreadable;
            // in the ordinary re-pair case the key is kept and every
            // envelope survives, still revealable after pairing again. Kind-
            // wide discarding held remote notes unconditionally here used to
            // silently lose that normal case's held arrival. Check what
            // actually remains instead of assuming: no pending envelopes
            // left means the unreadable-key branch ran and wiped them, so
            // any held note referencing them is already orphaned and must
            // go too; any envelope still pending means it is still
            // revealable, so its held note must be left alone.
            //
            // The forget above already committed -- this check (and the
            // discard it may trigger) is best-effort cleanup of another
            // subsystem's state, and a throw here must not turn an
            // already-committed forget into a reported failure or skip the
            // UI state update below. Fail closed on a throw: assume
            // envelopes remain, so a (possibly stale) held note is left
            // alone rather than risk discarding one that is still
            // revealable.
            var envelopesRemain = true;
            try
            {
                envelopesRemain = (await _context.RemoteEnvelopes.ListPendingAsync(cancellationToken)).Count > 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu forget-pairing envelope check failed: {0}", exception);
            }

            if (!envelopesRemain)
            {
                try
                {
                    await _context.DiscardHeldRemoteNotesAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    global::System.Diagnostics.Trace.TraceError("Dudu forget-pairing held-note discard failed: {0}", exception);
                }
            }
            await MutateAsync(() =>
            {
                PairingCode = null;
                CodeExpiresUtc = null;
                Sessions.Clear();
                SessionCount = 0;
                Availability = PairingAvailability.Offline;
                StatusReason = PairingStatusReason.None;
                OnPropertyChanged(nameof(IsPaired));
            }, cancellationToken);
        }, "otayyy pairing forgotten on this pc pair again anytime");

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        var action = PendingConfirmation;
        if (action == ConnectionConfirmationAction.None) return;

        var succeeded = action switch
        {
            ConnectionConfirmationAction.RevokeSessions => await RevokeSessionsAsync(cancellationToken),
            ConnectionConfirmationAction.DeleteRemoteDevice => await DeleteRemoteDeviceAsync(cancellationToken),
            ConnectionConfirmationAction.ForgetPairing => await ForgetPairingAsync(cancellationToken),
            _ => false,
        };
        if (succeeded) PendingConfirmation = ConnectionConfirmationAction.None;
    }

    private void RequestConfirmation(ConnectionConfirmationAction action) =>
        PendingConfirmation = action;

    private void CancelConfirmation() => PendingConfirmation = ConnectionConfirmationAction.None;
}
