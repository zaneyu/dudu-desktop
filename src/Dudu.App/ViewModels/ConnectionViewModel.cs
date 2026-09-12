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
    public PairingAvailability Availability { get => _availability; private set => SetProperty(ref _availability, value); }
    public string? PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }
    public DateTimeOffset? CodeExpiresUtc { get => _codeExpiresUtc; private set => SetProperty(ref _codeExpiresUtc, value); }
    public int SessionCount { get => _sessionCount; private set => SetProperty(ref _sessionCount, value); }
    public bool IsPaired => Availability == PairingAvailability.Available && SessionCount > 0;

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
                throw new NotSupportedException("Pairing is unavailable while the relay is offline.");
            }
        }, "Pairing code ready for ten minutes.");

    public Task RevokeSessionsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.RevokeSessionsWithResultAsync(cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "Sender-session revocation is unavailable.");
            Sessions.Clear();
            SessionCount = 0;
            OnPropertyChanged(nameof(IsPaired));
        }, "Sender sessions revoked.");

    public Task DeleteRemoteDeviceAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await _context.Pairing.DeleteRemoteDeviceWithResultAsync(cancellationToken: cancellationToken);
            if (!result.Completed) throw new NotSupportedException(result.ErrorMessage ?? "Remote-device deletion is unavailable.");
            PairingCode = null;
            CodeExpiresUtc = null;
            Sessions.Clear();
            SessionCount = 0;
            Availability = PairingAvailability.Offline;
            OnPropertyChanged(nameof(IsPaired));
        }, "Remote device data deleted.");
}
