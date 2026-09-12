using Dudu.App.Hosting;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage(HomeViewModel viewModel, StartupSettingsService startup)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
    }

    public HomeViewModel ViewModel { get; }
    public StartupSettingsService Startup { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshAsync();
        RefreshStartupRecovery();
    }

    private void RefreshStartupRecovery()
    {
        var visible = Startup.NeedsReconciliation;
        StartupRecoveryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StartupRecoveryMessage.Text = visible
            ? Startup.ReconciliationError ?? "Startup registration needs another try."
            : string.Empty;
    }

    private async void RetryStartupButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await Startup.RetryStartupRegistrationAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu startup retry failed: {0}", exception);
        }

        RefreshStartupRecovery();
    }

    private void CountdownList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            ViewModel.SelectCountdownCommand.Execute(list.SelectedItem as Dudu.Core.Models.Countdown);
        }
    }

    private void MoodBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox box
            && box.SelectedItem is ComboBoxItem item
            && Enum.TryParse<Dudu.Core.Models.MoodChoice>(item.Tag as string, out var mood))
        {
            ViewModel.SelectedMood = mood;
        }
    }

    private async void StartupToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox toggle || toggle.IsChecked is not bool enabled) return;
        try
        {
            await Startup.SetLaunchAtSignInAsync(enabled);
        }
        catch (Exception exception)
        {
            toggle.IsChecked = Startup.Current.LaunchAtSignIn;
            global::System.Diagnostics.Trace.TraceError("Dudu startup setting failed: {0}", exception);
        }
    }
}
