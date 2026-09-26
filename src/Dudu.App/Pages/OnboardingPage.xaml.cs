using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Dudu.App.Pages;

public sealed partial class OnboardingPage : Page
{
    private readonly OnboardingViewModel _viewModel;
    private readonly Action _completed;
    private readonly Func<bool, CancellationToken, Task>? _setUserVisible;
    private bool? _placementVisibilityRequested;

    // True while controls are being written from the draft (and during
    // InitializeComponent, when the XAML's own Slider Minimum/Value assignments
    // raise ValueChanged). Without it, constructing the page overwrote the loaded
    // pet scale with the slider's XAML default and pushed a placement preview to
    // the overlay before the user had even reached the placement step.
    private bool _suppressControlEvents = true;

    // True while a Next/Finish transition is in flight, so a double-click (or
    // Enter held down) cannot run the transition twice or let Back interleave.
    private bool _busy;

    // The completion callback swaps the settings shell to Home; it must run once
    // even if Finish is activated again while the first completion is settling.
    private bool _completionRaised;

    public OnboardingPage(
        OnboardingViewModel viewModel,
        Action completed,
        Func<bool, CancellationToken, Task>? setUserVisible = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _setUserVisible = setUserVisible;
        InitializeComponent();
        SyncControlsFromDraft();
        _suppressControlEvents = false;
        RefreshStep();
        RefreshStartupRecovery();
        // Replace the XAML placeholder names with the (empty or initial) text so no
        // status element is announced by its label alone.
        SetMessage(null);
        SetStatus(null);
        AutomationProperties.SetName(PairingStatus, PairingStatus.Text);
        Loaded += Page_Loaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        // First-run keyboard users land on the one field the first step needs.
        if (_viewModel.CurrentStep == OnboardingStep.Recipient)
        {
            FocusLater(RecipientNameBox);
        }
    }

    private async void RecommendedDefaultsButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            // Capture what is already typed first: SyncControlsFromDraft below
            // rewrites every control from the draft, and without this the name the
            // user had just entered was wiped back to the (empty) draft value.
            SyncDraftFromControls();
            await _viewModel.AcceptRecommendedDefaultsAsync();
            SyncControlsFromDraft();
            SetMessage(null);
            SetStatus("recommended defaults set. u can still change them on the next steps");
        }
        catch (Exception exception)
        {
            SetMessage("aiyo cant set the defaults yet ur choices stay");
            global::System.Diagnostics.Trace.TraceError("Dudu recommended defaults failed: {0}", exception);
        }
    }

    private async void RecommendedPlacementButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            // Same reason as above: keep the step's other unsaved choices.
            SyncDraftFromControls();
            await _viewModel.UseRecommendedPlacementAsync();
            SyncControlsFromDraft();
            SetMessage(null);
        }
        catch (OperationCanceledException)
        {
            SetMessage("placement paused ur draft still here");
        }
        catch (Exception exception)
        {
            SetMessage("aiyo cant preview that yet ur draft stays");
            global::System.Diagnostics.Trace.TraceInformation(
                "Dudu recommended placement failed: {0}",
                exception.Message);
        }
    }

    private async void PlacementScaleSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        UpdatePlacementScaleLabel(args.NewValue);
        if (_suppressControlEvents) return;
        _viewModel.PlacementScale = args.NewValue;
        await PreviewPlacementAsync();
    }

    private async void PairingCheckButton_Click(object sender, RoutedEventArgs args)
    {
        PairingCheckButton.IsEnabled = false;
        try
        {
            await _viewModel.RefreshPairingAsync();
            SetAnnouncedText(PairingStatus, _viewModel.PairingAvailability == PairingAvailability.Offline
                ? "pairing offline u can skip it"
                : "pairing ready whenever u are");
            SetMessage(null);
        }
        catch (Exception exception)
        {
            SetAnnouncedText(PairingStatus, "pairing unavailable now u can skip it");
            global::System.Diagnostics.Trace.TraceInformation("Dudu pairing check failed: {0}", exception.Message);
        }
        finally
        {
            PairingCheckButton.IsEnabled = true;
        }
    }

    private void SkipPairingButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.SkipPairing();
        SetAnnouncedText(PairingStatus, "skipped for now pair later ok");
        SetMessage(null);
    }

    private async void RetryStartupButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await _viewModel.RetryStartupRegistrationAsync();
            RefreshStartupRecovery();
        }
        catch (Exception exception)
        {
            RefreshStartupRecovery();
            global::System.Diagnostics.Trace.TraceError("Dudu startup retry during onboarding failed: {0}", exception);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        SyncDraftFromControls();
        _viewModel.Back();
        RefreshStep();
        if (!_viewModel.CanGoBack)
        {
            // Back just disabled itself on the first step; a disabled control
            // drops keyboard focus, so hand it to the step's next action.
            FocusLater(NextButton);
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs args)
    {
        await MoveNextAsync();
    }

    private async void CompleteButton_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _completionRaised) return;
        SyncDraftFromControls();
        SetBusy(true);
        var completed = false;
        try
        {
            completed = await _viewModel.CompleteAsync();
            SetMessage(completed ? null : _viewModel.ValidationMessage);
            if (completed)
            {
                try
                {
                    if (_setUserVisible is not null)
                    {
                        await _setUserVisible(true, CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    SetMessage("setup saved but dudu not shown yet");
                    global::System.Diagnostics.Trace.TraceError(
                        "Dudu could not show the companion after onboarding: {0}",
                        exception);
                }

                RaiseCompleted();
            }
        }
        catch (OperationCanceledException)
        {
            SetMessage("setup paused ur choices still here");
        }
        catch (Exception exception)
        {
            SetMessage("oh no cant finish setup ur choices stay");
            global::System.Diagnostics.Trace.TraceError("Dudu onboarding completion failed: {0}", exception);
        }
        finally
        {
            // A saved setup leaves the page; only a failed or rejected one needs
            // its controls back.
            if (!completed) SetBusy(false);
        }
    }

    private async Task MoveNextAsync()
    {
        if (_busy || _completionRaised) return;
        SyncDraftFromControls();
        SetBusy(true);
        try
        {
            var movedOrCompleted = await _viewModel.NextAsync();
            SetMessage(movedOrCompleted ? null : _viewModel.ValidationMessage);
            if (movedOrCompleted && _viewModel.IsComplete)
            {
                RaiseCompleted();
                return;
            }

            if (movedOrCompleted)
            {
                SetStatus(null);
            }
        }
        catch (OperationCanceledException)
        {
            SetMessage("setup paused ur choices still here");
        }
        catch (Exception exception)
        {
            SetMessage("cannot continue setup ur choices stay");
            global::System.Diagnostics.Trace.TraceError("Dudu onboarding navigation failed: {0}", exception);
        }
        finally
        {
            if (!_completionRaised)
            {
                SetBusy(false);
                RefreshStep();
            }
        }

        if (!_completionRaised && _viewModel.CurrentStep == OnboardingStep.Pairing)
        {
            // Next collapses on the last step while it still holds keyboard
            // focus; move focus to the step's own primary action instead of
            // dropping it.
            FocusLater(CompleteButton);
        }
    }

    private void RaiseCompleted()
    {
        if (_completionRaised) return;
        _completionRaised = true;
        _completed();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        NextButton.IsEnabled = !busy;
        CompleteButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy && _viewModel.CanGoBack;
    }

    private void RefreshStep()
    {
        RecipientStep.Visibility = Visibility.Collapsed;
        AppearanceStep.Visibility = Visibility.Collapsed;
        QuietHoursStep.Visibility = Visibility.Collapsed;
        RemindersStep.Visibility = Visibility.Collapsed;
        PlacementStep.Visibility = Visibility.Collapsed;
        PairingStep.Visibility = Visibility.Collapsed;

        switch (_viewModel.CurrentStep)
        {
            case OnboardingStep.Recipient: RecipientStep.Visibility = Visibility.Visible; break;
            case OnboardingStep.Appearance: AppearanceStep.Visibility = Visibility.Visible; break;
            case OnboardingStep.QuietHours: QuietHoursStep.Visibility = Visibility.Visible; break;
            case OnboardingStep.Reminders: RemindersStep.Visibility = Visibility.Visible; break;
            case OnboardingStep.Placement: PlacementStep.Visibility = Visibility.Visible; break;
            case OnboardingStep.Pairing: PairingStep.Visibility = Visibility.Visible; break;
        }

        SetAnnouncedText(OnboardingProgress, _viewModel.ProgressText);
        BackButton.IsEnabled = !_busy && _viewModel.CanGoBack;
        NextButton.Visibility = _viewModel.CurrentStep == OnboardingStep.Pairing
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompleteButton.Visibility = _viewModel.CurrentStep == OnboardingStep.Pairing
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshStartupRecovery();

        var shouldShowPlacementPet = _viewModel.CurrentStep is OnboardingStep.Placement or OnboardingStep.Pairing;
        if (_placementVisibilityRequested != shouldShowPlacementPet)
        {
            _placementVisibilityRequested = shouldShowPlacementPet;
            _ = SetPlacementVisibilityAsync(shouldShowPlacementPet);
        }
    }

    private async Task SetPlacementVisibilityAsync(bool visible)
    {
        if (_setUserVisible is null)
        {
            return;
        }

        try
        {
            await _setUserVisible(visible, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetMessage(visible
                ? "alala dudu not shown yet try again"
                : null);
            global::System.Diagnostics.Trace.TraceInformation(
                "Dudu placement visibility change failed: {0}",
                exception.Message);
        }
    }

    private void RefreshStartupRecovery()
    {
        var startup = _viewModel.StartupSettings;
        var visible = startup?.NeedsReconciliation == true;
        StartupRecoveryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SetAnnouncedText(StartupRecoveryMessage, visible
            ? startup!.ReconciliationError ?? "wait startup registration needs retry"
            : string.Empty);
    }

    private void SyncDraftFromControls()
    {
        _viewModel.RecipientName = RecipientNameBox.Text;
        _viewModel.ReducedMotion = ReducedMotionBox.IsChecked == true;
        _viewModel.QuietHoursEnabled = QuietHoursBox.IsChecked == true;
        // Unparseable times are flagged (and block the quiet-hours step) instead
        // of being silently dropped in favour of the previous value.
        _viewModel.TrySetQuietHoursText(QuietStartBox.Text, QuietEndBox.Text);
        _viewModel.HydrationRemindersEnabled = HydrationBox.IsChecked == true;
        _viewModel.BreakRemindersEnabled = BreakBox.IsChecked == true;
        // A cleared NumberBox reports NaN; the view model rejects it rather than
        // letting a cast turn it into a limit of 0.
        _viewModel.SetLocalNoteDailyLimitInput(NoteLimitBox.Value);
        _viewModel.PlacementScale = PlacementScaleSlider.Value;
        _viewModel.HidePetDuringFullscreen = HideFullscreenBox.IsChecked == true;
        _viewModel.LaunchAtSignIn = LaunchAtSignInBox.IsChecked == true;
        if (ThemeBox.SelectedItem is ComboBoxItem themeItem
            && Enum.TryParse<AppTheme>(themeItem.Tag as string, out var theme))
        {
            _viewModel.Theme = theme;
        }
    }

    private void SyncControlsFromDraft()
    {
        var previous = _suppressControlEvents;
        _suppressControlEvents = true;
        try
        {
            RecipientNameBox.Text = _viewModel.RecipientName;
            ReducedMotionBox.IsChecked = _viewModel.ReducedMotion;
            QuietHoursBox.IsChecked = _viewModel.QuietHoursEnabled;
            QuietStartBox.Text = _viewModel.QuietHoursStart.ToString("HH:mm");
            QuietEndBox.Text = _viewModel.QuietHoursEnd.ToString("HH:mm");
            HydrationBox.IsChecked = _viewModel.HydrationRemindersEnabled;
            BreakBox.IsChecked = _viewModel.BreakRemindersEnabled;
            NoteLimitBox.Value = _viewModel.LocalNoteDailyLimit;
            PlacementScaleSlider.Value = _viewModel.PlacementScale;
            UpdatePlacementScaleLabel(PlacementScaleSlider.Value);
            HideFullscreenBox.IsChecked = _viewModel.HidePetDuringFullscreen;
            LaunchAtSignInBox.IsChecked = _viewModel.LaunchAtSignIn;
            ThemeBox.SelectedIndex = _viewModel.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };
        }
        finally
        {
            _suppressControlEvents = previous;
        }
    }

    private void UpdatePlacementScaleLabel(double scale)
    {
        // Runs during InitializeComponent too, before later-declared named
        // elements exist.
        if (PlacementScaleLabel is null || !double.IsFinite(scale)) return;
        var text = $"{Math.Round(scale * 100):0}%";
        PlacementScaleLabel.Text = text;
        AutomationProperties.SetName(PlacementScaleLabel, $"pet size {text}");
    }

    private void SetMessage(string? message)
    {
        SetAnnouncedText(OnboardingValidationMessage, message);
    }

    private void SetStatus(string? message)
    {
        SetAnnouncedText(OnboardingStatusMessage, message);
    }

    /// <summary>Sets a status TextBlock's text and makes it the element's UIA name.
    /// A static AutomationProperties.Name ("validation message", "pairing is
    /// optional") overrides the TextBlock's text, so screen readers read the label
    /// instead of the actual error, and a live region is only announced when
    /// LiveRegionChanged is raised.</summary>
    private static void SetAnnouncedText(TextBlock block, string? text)
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
                "Dudu onboarding announcement failed: {0}",
                exception.GetType().Name);
        }
    }

    /// <summary>Focuses after the current layout pass, once a control that was
    /// just made visible or re-enabled can actually accept focus.</summary>
    private void FocusLater(Control control)
    {
        var queue = DispatcherQueue;
        if (queue is null || !queue.TryEnqueue(() => control.Focus(FocusState.Programmatic)))
        {
            control.Focus(FocusState.Programmatic);
        }
    }

    private async Task PreviewPlacementAsync()
    {
        try
        {
            await _viewModel.PreviewPlacementAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceInformation(
                "Dudu placement preview failed: {0}",
                exception.Message);
        }
    }
}
