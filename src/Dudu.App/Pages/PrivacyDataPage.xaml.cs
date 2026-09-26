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

    // SettingsWindow caches this page, so Loaded fires on every visit. Without this, a
    // destructive confirmation requested on an earlier visit stayed armed when she came back.
    private void Page_Loaded(object sender, RoutedEventArgs args) => ViewModel.ResetTransientState();
}
