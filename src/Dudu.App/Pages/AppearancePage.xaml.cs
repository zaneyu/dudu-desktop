using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class AppearancePage : Page
{
    private bool _syncingTheme;

    public AppearancePage(AppearanceViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
    }

    public AppearanceViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshAsync();
        SyncThemeFromViewModel();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingTheme || !IsLoaded || sender is not ComboBox box
            || box.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse<AppTheme>(item.Tag as string, out var theme)) return;

        ViewModel.Theme = theme;
    }

    private void SyncThemeFromViewModel()
    {
        _syncingTheme = true;
        try
        {
            ThemeBox.SelectedIndex = ViewModel.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };
        }
        finally
        {
            _syncingTheme = false;
        }
    }
}
