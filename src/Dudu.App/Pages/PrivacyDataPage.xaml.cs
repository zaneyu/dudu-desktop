using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class PrivacyDataPage : Page
{
    public PrivacyDataPage(PrivacyDataViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
    }

    public PrivacyDataViewModel ViewModel { get; }

    // SettingsWindow caches this page, so Loaded fires on every visit: disarm
    // any destructive confirmation left pending from a previous visit.
    private void Page_Loaded(object sender, RoutedEventArgs args) =>
        ViewModel.ResetPendingConfirmation();
}
