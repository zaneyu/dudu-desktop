using System.ComponentModel;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class ConnectionPage : Page
{
    // Moves the "n min left" countdown and retires an expired code while the page is open;
    // without it the code stayed on screen as usable until she navigated away and back.
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public ConnectionPage(ConnectionViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        _expiryTimer.Tick += ExpiryTimer_Tick;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public ConnectionViewModel ViewModel { get; }

    // SettingsWindow caches this page and swaps it in and out of the content
    // frame, so Loaded/Unloaded fire on every visit. The view-model
    // subscription is live only while the page is in the tree; the page's own
    // Loaded/Unloaded handlers stay attached so revisits still refresh.
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _expiryTimer.Stop();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _expiryTimer.Start();
        await ViewModel.RefreshAsync();
        RefreshStatusText();
    }

    private void ExpiryTimer_Tick(object? sender, object args)
    {
        // A throw on a DispatcherTimer tick is unhandled and repeats every interval; this is
        // display-only work, so a failure just skips the tick.
        try
        {
            ViewModel.UpdateCodeExpiry();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu connection expiry tick failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.Availability)
            or nameof(ConnectionViewModel.StatusReason)
            or nameof(ConnectionViewModel.PairingCode)
            or nameof(ConnectionViewModel.CodeExpiresUtc)
            or nameof(ConnectionViewModel.PairingCodeText)
            or nameof(ConnectionViewModel.CodeExpiryText)
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
        // The pairing-code and expiry lines are view-model text too now, for the same reason:
        // the page's own copies never learned that a code can expire.
        SetText(ConnectionAvailability, ViewModel.AvailabilityText);
        SetText(ConnectionPairingCode, ViewModel.PairingCodeText);
        SetText(ConnectionCodeExpiry, ViewModel.CodeExpiryText);
        SetText(ConnectionSessionCount, ViewModel.SessionCountText);
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
