using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Abstractions;

namespace Dudu.App.ViewModels;

public sealed class ConnectionViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private PairingAvailability _availability = PairingAvailability.Offline;
    private string? _pairingCode;
    private DateTimeOffset? _codeExpiresUtc;
    private int _sessionCount;

    public ConnectionViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        CreateCodeCommand = new AsyncRelayCommand(() => CreateCodeAsync(CancellationToken.None));
        RevokeSessionsCommand = new AsyncRelayCommand(() => RevokeSessionsAsync(CancellationToken.None));
        DeleteRemoteDeviceCommand = new AsyncRelayCommand(() => DeleteRemoteDeviceAsync(CancellationToken.None));
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
    public string AvailabilityText => Availability switch
    {
        PairingAvailability.Available => "pairing is available",
        PairingAvailability.NeedsRepair => "pairing needs a bit of attention before it can be used",
        _ => "pairing is offline dudu still works fine on this pc",
    };
    public string PairingCodeText => string.IsNullOrWhiteSpace(PairingCode)
        ? "no pairing code active right now"
        : $"pairing code {PairingCode}";
    public string CodeExpiryText => CodeExpiresUtc is null
        ? "no active pairing code expiry"
        : $"code expires {CodeExpiresUtc.Value.ToLocalTime():g}";
    public string SessionCountText => SessionCount switch
    {
        0 => "no paired sender sessions",
        1 => "1 paired sender session",
        _ => $"{SessionCount} paired sender sessions",
    };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            Availability = await _context.Pairing.GetStateAsync(cancellationToken);
            Sessions.Clear();
            try
            {
                foreach (var session in await _context.Pairing.ListSessionsAsync(cancellationToken)) Sessions.Add(session);
                SessionCount = Sessions.Count;
            }
            catch (NotSupportedException)
            {
                SessionCount = 0;
            }
            OnPropertyChanged(nameof(IsPaired));
        });
    }

    public Task CreateCodeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.CreateCodeAsync(cancellationToken);
            Availability = result.Availability;
            PairingCode = result.Code;
            CodeExpiresUtc = result.ExpiresUtc;
            if (result.Availability != PairingAvailability.Available ||
                string.IsNullOrWhiteSpace(result.Code) || result.ExpiresUtc is null)
            {
                throw new NotSupportedException("aiyo pairing unavailable while the relay is offline");
            }
        }, "oki pairing code ready for 10 min");

    public Task RevokeSessionsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.RevokeSessionsWithResultAsync(cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "aiyo cant revoke sender sessions right now");
            Sessions.Clear();
            SessionCount = 0;
            OnPropertyChanged(nameof(IsPaired));
        }, "sender sessions revoked le");

    public Task DeleteRemoteDeviceAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.DeleteRemoteDeviceWithResultAsync(cancellationToken: cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "aiyo cant delete remote device data right now");
            PairingCode = null;
            CodeExpiresUtc = null;
            Sessions.Clear();
            SessionCount = 0;
            Availability = PairingAvailability.Offline;
            OnPropertyChanged(nameof(IsPaired));
        }, "remote device data deleted le");
}
