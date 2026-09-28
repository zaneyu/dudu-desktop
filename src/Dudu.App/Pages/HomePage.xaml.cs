using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly OverlayCommandRouter? _overlayCommands;
    private DispatcherTimer? _focusCountdownTimer;
    private DispatcherTimer? _partnerClockTimer;

    public HomePage(
        HomeViewModel viewModel,
        OverlayCommandRouter? overlayCommands = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _overlayCommands = overlayCommands;
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public HomeViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        // The focus line was computed once per refresh, so a running session sat at
        // "25 min left" for as long as Home stayed open. Tick it while Home is shown.
        if (_focusCountdownTimer is null)
        {
            _focusCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _focusCountdownTimer.Tick += FocusCountdownTimer_Tick;
        }
        _focusCountdownTimer.Start();

        // The UK clock card shows seconds, so it ticks twice a second while Home is
        // shown; the view model only raises the properties whose text changed.
        if (_partnerClockTimer is null)
        {
            _partnerClockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _partnerClockTimer.Tick += PartnerClockTimer_Tick;
        }
        RefreshPartnerClock();
        _partnerClockTimer.Start();

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
    // Loaded/Unloaded fire on every visit; the countdown only runs while Home is shown.
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        _focusCountdownTimer?.Stop();
        _partnerClockTimer?.Stop();
    }

    /// <summary>Called when the hosting window closes: Unloaded is not guaranteed for a
    /// closed window's content, and the UI thread (and so this timer) outlives it.</summary>
    public void StopFocusCountdown()
    {
        _focusCountdownTimer?.Stop();
        _partnerClockTimer?.Stop();
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

    private void FocusCountdownTimer_Tick(object? sender, object args)
    {
        // A throw on a DispatcherTimer tick is unhandled and repeats every interval;
        // this is display-only work, so a failure just skips the tick.
        try
        {
            if (ViewModel.ActiveFocus is { Status: Dudu.Core.Models.FocusStatus.Running })
            {
                ViewModel.RefreshFocusCountdown();
            }
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceWarning(
                "Dudu home focus tick failed: {0} 0x{1:X8}",
                exception.GetType().Name,
                exception.HResult);
        }
    }

    private void CountdownList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            ViewModel.SelectCountdownCommand.Execute(list.SelectedItem as Dudu.Core.Models.Countdown);
            CountdownTargetBox.Text = ViewModel.CountdownTargetUtc?.ToLocalTime().ToString("g") ?? string.Empty;
        }
    }

    /// <summary>CountdownTargetBox is parsed from code-behind rather than x:Bound, so
    /// when the view model moves the target on its own -- SaveCountdownAsync clears it
    /// after a save -- the box has to follow. Otherwise it kept showing the old date
    /// while the view model held null, and the next save silently used "tomorrow".
    /// Text that already represents the view model's target (including text the user
    /// is typing) is left untouched.</summary>
    private void ViewModel_PropertyChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(HomeViewModel.CountdownTargetUtc)) return;
        try
        {
            if (HomeViewModel.CountdownTargetTextMatches(CountdownTargetBox.Text, ViewModel.CountdownTargetUtc)) return;
            CountdownTargetBox.Text = ViewModel.CountdownTargetUtc?.ToLocalTime().ToString("g") ?? string.Empty;
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu countdown target sync failed: {0}", exception);
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
        SetAccessibleText(CountdownTargetValidation, message);
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
            SetAccessibleText(HomeActionStatus, $"{ActionBubbleLayout.Label(action)} ready le");
        }
        catch (Exception exception)
        {
            SetAccessibleText(HomeActionStatus, HomeViewModel.DescribeError(exception));
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
            SetAccessibleText(HomeActionStatus, $"{ActionBubbleLayout.ComfortLabel(action)} ready le");
        }
        catch (Exception exception)
        {
            SetAccessibleText(HomeActionStatus, HomeViewModel.DescribeError(exception));
            global::System.Diagnostics.Trace.TraceError("Dudu comfort action failed: {0}", exception);
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
