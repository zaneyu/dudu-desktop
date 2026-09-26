using System.ComponentModel;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly OverlayCommandRouter? _overlayCommands;
    private bool _suppressStartupToggle;
    private bool _syncingCountdownTarget;
    private bool _applyingCountdownTargetFromBox;

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
        Unloaded += Page_Unloaded;
    }

    public HomeViewModel ViewModel { get; }
    public StartupSettingsService Startup { get; }

    // SettingsWindow caches this page, so Loaded/Unloaded fire on every visit;
    // the view-model subscription is live only while the page is in the tree.
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Run before the (possibly slow, possibly failing) view-model refresh so the
        // checkbox reflects the real startup registration immediately: the XAML no
        // longer binds StartupToggle.IsChecked, so until this runs it would otherwise
        // sit at the CheckBox default instead of the actual state.
        RefreshStartupRecovery();

        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu home refresh failed: {0}", exception);
        }
    }

    private void RefreshStartupRecovery()
    {
        // Called from async void handlers (Page_Loaded, RetryStartupButton_Click,
        // StartupToggle_Changed) with no surrounding try/catch of their own -- an
        // unhandled throw here would crash the process, so this guards its own body.
        try
        {
            var visible = Startup.NeedsReconciliation;
            StartupRecoveryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            StartupRecoveryMessage.Text = visible
                ? Startup.ReconciliationError ?? "aiyo startup registration needs another try"
                : string.Empty;

            // Sets the checkbox's initial and every subsequent state imperatively (the XAML
            // does not bind IsChecked at all -- x:Bind evaluates during InitializeComponent,
            // before this suppression flag exists, so a OneTime IsChecked binding would fire
            // Checked/Unchecked and perform a real OS startup-registration write on every
            // Home load). Also keeps the checkbox on the last applied state after a failed
            // write is reverted and "try again" then succeeds.
            _suppressStartupToggle = true;
            try
            {
                StartupToggle.IsChecked = Startup.ActualLaunchAtSignIn;
            }
            finally
            {
                _suppressStartupToggle = false;
            }
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu startup recovery refresh failed: {0}", exception);
        }
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
        // The target box is resynced from ViewModel_PropertyChanged, which also
        // covers programmatic clears (after save, delete or "new countdown").
        if (sender is ListView list)
        {
            ViewModel.SelectCountdownCommand.Execute(list.SelectedItem as Dudu.Core.Models.Countdown);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(HomeViewModel.CountdownTargetUtc) when !_applyingCountdownTargetFromBox:
                // After a save the view model clears the target; without this the
                // unbound box kept the old text while the value was null, so the
                // next countdown was saved with the wrong (or no) date.
                SyncCountdownTargetBox();
                break;
            case nameof(HomeViewModel.SelectedCountdown):
                // Also clears a stale validation message left by half-typed text.
                SyncCountdownTargetBox();
                if (!Equals(CountdownList.SelectedItem, ViewModel.SelectedCountdown))
                {
                    CountdownList.SelectedItem = ViewModel.SelectedCountdown;
                }

                break;
        }
    }

    private void SyncCountdownTargetBox()
    {
        _syncingCountdownTarget = true;
        try
        {
            CountdownTargetBox.Text = ViewModel.CountdownTargetUtc?.ToLocalTime().ToString("g") ?? string.Empty;
            SetCountdownTargetValidation(true, null);
        }
        finally
        {
            _syncingCountdownTarget = false;
        }
    }

    private void CountdownTarget_Changed(object sender, TextChangedEventArgs args)
    {
        if (_syncingCountdownTarget || sender is not TextBox box) return;
        _applyingCountdownTargetFromBox = true;
        try
        {
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
        finally
        {
            _applyingCountdownTargetFromBox = false;
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
            HomeActionStatus.Text = HomeViewModel.DescribeError(exception);
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
            HomeActionStatus.Text = HomeViewModel.DescribeError(exception);
            global::System.Diagnostics.Trace.TraceError("Dudu comfort action failed: {0}", exception);
        }
    }

    private async void StartupToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (_suppressStartupToggle) return;
        if (sender is not CheckBox toggle || toggle.IsChecked is not bool enabled) return;
        try
        {
            await Startup.SetLaunchAtSignInAsync(enabled);
        }
        catch (Exception exception)
        {
            // Current.LaunchAtSignIn already records the desired (failed) state, so
            // reverting to it would be a no-op. Fall back to what the OS actually has
            // registered, and suppress this handler first so setting IsChecked here
            // does not re-enter it through the Checked/Unchecked events.
            _suppressStartupToggle = true;
            try
            {
                toggle.IsChecked = Startup.ActualLaunchAtSignIn;
            }
            finally
            {
                _suppressStartupToggle = false;
            }

            global::System.Diagnostics.Trace.TraceError("Dudu startup setting failed: {0}", exception);
        }

        RefreshStartupRecovery();
    }

}
