using System.ComponentModel;
using Dudu.App.ViewModels;
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
            or nameof(ConnectionViewModel.StatusReason)
            or nameof(ConnectionViewModel.PairingCode)
            or nameof(ConnectionViewModel.CodeExpiresUtc)
            or nameof(ConnectionViewModel.SessionCount))
        {
            RefreshStatusText();
        }
    }

    private void RefreshStatusText()
    {
        // Reviews C1/I9: one source of truth. This used to be a second, subtly different copy
        // of the view model's switch, so a reason the view model knows about -- no relay
        // configured, or a relay answering with something unreadable -- never reached the page.
        ConnectionAvailability.Text = ViewModel.AvailabilityText;
        ConnectionPairingCode.Text = string.IsNullOrWhiteSpace(ViewModel.PairingCode)
            ? "no code yet ah"
            : $"pairing code {ViewModel.PairingCode}";
        ConnectionCodeExpiry.Text = ViewModel.CodeExpiresUtc is { } expires
            ? $"code expires at {expires.ToLocalTime():g}"
            : "no code expiry yet";

        var count = ViewModel.SessionCount;
        ConnectionSessionCount.Text = count switch
        {
            0 => "no sender sessions yet ah",
            1 => "1 paired sender session",
            _ => $"{count} paired sender sessions",
        };
    }
}
