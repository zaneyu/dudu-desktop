using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class ConnectionPage : Page
{
    public ConnectionPage(ConnectionViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += Page_Loaded;
    }

    public ConnectionViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshAsync();
        RefreshStatusText();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.Availability)
            or nameof(ConnectionViewModel.PairingCode)
            or nameof(ConnectionViewModel.CodeExpiresUtc)
            or nameof(ConnectionViewModel.SessionCount))
        {
            RefreshStatusText();
        }
    }

    private void RefreshStatusText()
    {
        ConnectionAvailability.Text = ViewModel.Availability switch
        {
            PairingAvailability.Available => "pairing is available",
            PairingAvailability.NeedsRepair => "pairing needs repair",
            _ => "pairing is offline",
        };
        ConnectionPairingCode.Text = string.IsNullOrWhiteSpace(ViewModel.PairingCode)
            ? "no active pairing code"
            : $"pairing code {ViewModel.PairingCode}";
        ConnectionCodeExpiry.Text = ViewModel.CodeExpiresUtc is { } expires
            ? $"code expires at {expires.ToLocalTime():g}"
            : "no active code expiry";

        var count = ViewModel.SessionCount;
        ConnectionSessionCount.Text = count switch
        {
            0 => "no paired sender sessions",
            1 => "1 paired sender session",
            _ => $"{count} paired sender sessions",
        };
    }
}
