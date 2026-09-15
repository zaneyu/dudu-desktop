using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Abstractions;

namespace Dudu.App.ViewModels;

public sealed class ConnectionViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private PairingAvailability _availability = PairingAvailability.Offline;
    private PairingStatusReason _statusReason = PairingStatusReason.None;
    private string? _pairingCode;
    private DateTimeOffset? _codeExpiresUtc;
    private int _sessionCount;

    public ConnectionViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        CreateCodeCommand = new AsyncRelayCommand((CancellationToken ct) => CreateCodeAsync(ct));
        RevokeSessionsCommand = new AsyncRelayCommand((CancellationToken ct) => RevokeSessionsAsync(ct));
        DeleteRemoteDeviceCommand = new AsyncRelayCommand((CancellationToken ct) => DeleteRemoteDeviceAsync(ct));
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand CreateCodeCommand { get; }
    public IAsyncRelayCommand RevokeSessionsCommand { get; }
    public IAsyncRelayCommand DeleteRemoteDeviceCommand { get; }
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

    public Task RevokeSessionsAsync(CancellationToken cancellationToken = default) =>
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

    public Task DeleteRemoteDeviceAsync(CancellationToken cancellationToken = default) =>
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
}
