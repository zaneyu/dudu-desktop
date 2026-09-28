using System.ComponentModel;
using Dudu.App.Hosting;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _syncingTheme;
    private bool _suppressStartupToggle;

    // Moves the "n min left" countdown and retires an expired code while the page is open;
    // without it the code stayed on screen as usable until she navigated away and back.
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public SettingsPage(SettingsViewModel viewModel, StartupSettingsService startup)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        InitializeComponent();
        DataContext = ViewModel;
        _expiryTimer.Tick += ExpiryTimer_Tick;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public SettingsViewModel ViewModel { get; }
    public StartupSettingsService Startup { get; }

    /// <summary>Re-reads the partner connection section only. SettingsWindow calls this
    /// when an already-open window is shown again; the look-and-motion fields are left
    /// alone because reloading them would overwrite unsaved edits.</summary>
    public async Task RefreshConnectionAsync()
    {
        await ViewModel.Connection.RefreshAsync();
        RefreshConnectionStatusText();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        // SettingsWindow caches this page and swaps it in and out of the content
        // frame, so Loaded/Unloaded fire on every visit. The connection subscription
        // is live only while the page is in the tree.
        ViewModel.Connection.PropertyChanged -= Connection_PropertyChanged;
        ViewModel.Connection.PropertyChanged += Connection_PropertyChanged;
        _expiryTimer.Start();

        // Run before the (possibly slow, possibly failing) view-model refresh so the
        // checkbox reflects the real startup registration immediately: the XAML does
        // not bind StartupToggle.IsChecked, so until this runs it would otherwise sit
        // at the CheckBox default instead of the actual state.
        RefreshStartupRecovery();

        await ViewModel.RefreshAsync();
        SyncThemeFromViewModel();
        await RefreshConnectionAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        ViewModel.Connection.PropertyChanged -= Connection_PropertyChanged;
        _expiryTimer.Stop();
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

    private void ExpiryTimer_Tick(object? sender, object args)
    {
        // A throw on a DispatcherTimer tick is unhandled and repeats every interval; this is
        // display-only work, so a failure just skips the tick.
        try
        {
            ViewModel.Connection.UpdateCodeExpiry();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu connection expiry tick failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.Availability)
            or nameof(ConnectionViewModel.StatusReason)
            or nameof(ConnectionViewModel.PairingCode)
            or nameof(ConnectionViewModel.CodeExpiresUtc)
            or nameof(ConnectionViewModel.PairingCodeText)
            or nameof(ConnectionViewModel.CodeExpiryText)
            or nameof(ConnectionViewModel.SessionCount))
        {
            RefreshConnectionStatusText();
        }
    }

    private void RefreshConnectionStatusText()
    {
        // Reviews C1/I9: one source of truth. This used to be a second, subtly different copy
        // of the view model's switch, so a reason the view model knows about -- no relay
        // configured, or a relay answering with something unreadable -- never reached the page.
        // The pairing-code and expiry lines are view-model text too now, for the same reason:
        // the page's own copies never learned that a code can expire.
        var connection = ViewModel.Connection;
        SetText(ConnectionAvailability, connection.AvailabilityText);
        SetText(ConnectionPairingCode, connection.PairingCodeText);
        SetText(ConnectionCodeExpiry, connection.CodeExpiryText);
        SetText(ConnectionSessionCount, connection.SessionCountText);
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
            SetText(StartupRecoveryMessage, visible
                ? Startup.ReconciliationError ?? StartupSettingsService.RetryStartupMessage
                : string.Empty);

            // Sets the checkbox's initial and every subsequent state imperatively (the XAML
            // does not bind IsChecked at all -- x:Bind evaluates during InitializeComponent,
            // before this suppression flag exists, so a OneTime IsChecked binding would fire
            // Checked/Unchecked and perform a real OS startup-registration write on every
            // Settings load). Also keeps the checkbox on the last applied state after a
            // failed write is reverted and "try again" then succeeds.
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

    // The XAML gives these TextBlocks a fixed AutomationProperties.Name ("pairing code"), and a
    // Name overrides a TextBlock's text for screen readers -- Narrator announced "pairing code"
    // but never the code itself, nor the status or session count. Keep the accessible name in
    // step with what is on screen.
    private static void SetText(TextBlock block, string text)
    {
        block.Text = text;
        AutomationProperties.SetName(block, text);
    }
}
