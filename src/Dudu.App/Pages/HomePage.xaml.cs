using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly OverlayCommandRouter? _overlayCommands;

    public HomePage(
        HomeViewModel viewModel,
        StartupSettingsService startup,
        OverlayCommandRouter? overlayCommands = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _overlayCommands = overlayCommands;
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
    }

    public HomeViewModel ViewModel { get; }
    public StartupSettingsService Startup { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu home refresh failed: {0}", exception);
        }

        RefreshStartupRecovery();
    }

    private void RefreshStartupRecovery()
    {
        var visible = Startup.NeedsReconciliation;
        StartupRecoveryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StartupRecoveryMessage.Text = visible
            ? Startup.ReconciliationError ?? "aiyo startup registration needs another try"
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
            CountdownTargetBox.Text = ViewModel.CountdownTargetUtc?.ToLocalTime().ToString("g") ?? string.Empty;
        }
    }

    private void CountdownTarget_Changed(object sender, TextChangedEventArgs args)
    {
        if (sender is not TextBox box) return;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            ViewModel.CountdownTargetUtc = null;
            SetCountdownTargetValidation(true, null);
        }
        else if (DateTimeOffset.TryParse(box.Text, out var target))
        {
            ViewModel.CountdownTargetUtc = target;
            SetCountdownTargetValidation(true, null);
        }
        else
        {
            SetCountdownTargetValidation(false, $"use a date like {DateHintExample()}");
        }
    }

    private void SetCountdownTargetValidation(bool isValid, string? message)
    {
        HomeSaveCountdownButton.IsEnabled = isValid;
        CountdownTargetValidation.Text = message ?? string.Empty;
        CountdownTargetValidation.Visibility = isValid ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Locale-correct date example, rendered with the same general pattern the
    /// free-text parser accepts, so following the hint always parses.</summary>
    private static string DateHintExample() =>
        new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("g");

    private void MoodBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox box
            && box.SelectedItem is ComboBoxItem item
            && Enum.TryParse<Dudu.Core.Models.MoodChoice>(item.Tag as string, out var mood))
        {
            ViewModel.SelectedMood = mood;
        }
    }

    private async void OverlayAction_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button
            || !Enum.TryParse<OverlayAction>(button.Tag as string, out var action)) return;

        try
        {
            var commands = _overlayCommands ?? throw new InvalidOperationException(
                "aiyo dudus action controls not ready yet");
            await commands.ExecuteAccessibleAsync(action);
            HomeActionStatus.Text = $"{ActionBubbleLayout.Label(action)} ready le";
        }
        catch (Exception exception)
        {
            HomeActionStatus.Text = exception.Message;
            global::System.Diagnostics.Trace.TraceError("Dudu action failed: {0}", exception);
        }
    }

    private async void ComfortAction_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button
            || !Enum.TryParse<ComfortAction>(button.Tag as string, out var action)) return;

        try
        {
            var commands = _overlayCommands ?? throw new InvalidOperationException(
                "oh no dudus comfort controls not ready");
            await commands.ExecuteComfortAccessibleAsync(action);
            HomeActionStatus.Text = $"{ActionBubbleLayout.ComfortLabel(action)} ready le";
        }
        catch (Exception exception)
        {
            HomeActionStatus.Text = exception.Message;
            global::System.Diagnostics.Trace.TraceError("Dudu comfort action failed: {0}", exception);
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
            // Current.LaunchAtSignIn already records the desired (failed) state, so
            // reverting to it would be a no-op. Fall back to what the OS actually has
            // registered, and detach the handler first so setting IsChecked here does
            // not re-enter this method through the Checked/Unchecked events.
            toggle.Checked -= StartupToggle_Changed;
            toggle.Unchecked -= StartupToggle_Changed;
            toggle.IsChecked = Startup.ActualLaunchAtSignIn;
            toggle.Checked += StartupToggle_Changed;
            toggle.Unchecked += StartupToggle_Changed;
            global::System.Diagnostics.Trace.TraceError("Dudu startup setting failed: {0}", exception);
        }

        RefreshStartupRecovery();
    }

}
