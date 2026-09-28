using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dudu.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly OverlayCommandRouter? _overlayCommands;
    private DispatcherTimer? _partnerClockTimer;

    public HomePage(
        HomeViewModel viewModel,
        OverlayCommandRouter? overlayCommands = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _overlayCommands = overlayCommands;
        // Before InitializeComponent: x:Bind reads Breathing.* from the start.
        Breathing = new BreathingPanelPresenter(overlayCommands, RunOnUiThread);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public HomeViewModel ViewModel { get; }

    /// <summary>The "breathe with me" panel shown while the exercise runs.</summary>
    public BreathingPanelPresenter Breathing { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        // The UK clock card shows seconds, so it ticks twice a second while Home is
        // shown; the view model only raises the properties whose text changed.
        if (_partnerClockTimer is null)
        {
            _partnerClockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _partnerClockTimer.Tick += PartnerClockTimer_Tick;
        }
        RefreshPartnerClock();
        _partnerClockTimer.Start();
        Breathing.Attach();

        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu home refresh failed: {0}", exception);
        }
    }

    // SettingsWindow caches this page and swaps it in and out of the content frame, so
    // Loaded/Unloaded fire on every visit; the clock only ticks while Home is shown.
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        _partnerClockTimer?.Stop();
        StopBreathingOnLeave();
    }

    /// <summary>Called when the hosting window closes: Unloaded is not guaranteed for a
    /// closed window's content, and the UI thread (and so this timer) outlives it.</summary>
    public void StopPartnerClock()
    {
        _partnerClockTimer?.Stop();
        StopBreathingOnLeave();
    }

    /// <summary>Leaving Home ends a running breathing exercise (only a running
    /// one, so a tiny hug in progress is not cut short) and stops listening
    /// until Home is shown again.</summary>
    private void StopBreathingOnLeave()
    {
        _ = ObserveBreathingStopAsync(Breathing.StopIfBreathingAsync());
        Breathing.Detach();
    }

    private static async Task ObserveBreathingStopAsync(Task stop)
    {
        try
        {
            await stop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu breathing stop failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    private void RunOnUiThread(Action action)
    {
        var queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ = queue.TryEnqueue(() => action());
        }
    }

    /// <summary>Mirrors the settings window's current Dudu frame into Home's big
    /// frame, so both always show the same pose.</summary>
    public void ShowDuduFrame(ImageSource? frame, string accessibleName)
    {
        HomeDuduImage.Source = frame;
        AutomationProperties.SetName(HomeDuduImage, accessibleName);
    }

    private void PartnerClockTimer_Tick(object? sender, object args) => RefreshPartnerClock();

    private void RefreshPartnerClock()
    {
        // Display-only: a throw on a DispatcherTimer tick would repeat every interval.
        try
        {
            ViewModel.RefreshPartnerClock();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu home partner clock tick failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
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
            SetAccessibleText(HomeActionStatus, $"{OverlayCommandRouter.Label(action)} ready le");
        }
        catch (OperationCanceledException) when (action == OverlayAction.BreatheWithMe)
        {
            // Stop (or leaving Home, or starting it again) cancels the running
            // exercise: that is the normal way it ends early, not an error.
            SetAccessibleText(HomeActionStatus, "breathing stopped, well done");
        }
        catch (Exception exception)
        {
            SetAccessibleText(HomeActionStatus, HomeViewModel.DescribeError(exception));
            global::System.Diagnostics.Trace.TraceError("Dudu action failed: {0}", exception);
        }
    }

    private async void BreathingStop_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await Breathing.StopAsync();
        }
        catch (Exception exception)
        {
            SetAccessibleText(HomeActionStatus, HomeViewModel.DescribeError(exception));
            global::System.Diagnostics.Trace.TraceError("Dudu breathing stop failed: {0}", exception);
        }
    }

    /// <summary>Code-behind status text must also become the element's UIA name: a
    /// static AutomationProperties.Name ("action status") overrides the TextBlock's
    /// text, so Narrator and the live-region announcement read the label instead of
    /// the actual message. Raises LiveRegionChanged so a polite/assertive region is
    /// actually announced.</summary>
    private static void SetAccessibleText(TextBlock block, string? text)
    {
        block.Text = text ?? string.Empty;
        AutomationProperties.SetName(block, block.Text);
        if (block.Text.Length == 0) return;
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(block)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(block);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceInformation(
                "Dudu live region announcement failed: {0}",
                exception.GetType().Name);
        }
    }
}
