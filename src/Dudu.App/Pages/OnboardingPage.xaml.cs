using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Dudu.App.Pages;

public sealed partial class OnboardingPage : Page
{
    private readonly OnboardingViewModel _viewModel;
    private readonly Action _completed;

    public OnboardingPage(OnboardingViewModel viewModel, Action completed)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        InitializeComponent();
        SyncControlsFromDraft();
        RefreshStep();
        RefreshStartupRecovery();
    }

    private async void RecommendedDefaultsButton_Click(object sender, RoutedEventArgs args)
    {
        await _viewModel.AcceptRecommendedDefaultsAsync();
        SyncControlsFromDraft();
        SetMessage(null);
    }

    private async void RecommendedPlacementButton_Click(object sender, RoutedEventArgs args)
    {
        await _viewModel.UseRecommendedPlacementAsync();
        SyncControlsFromDraft();
        SetMessage(null);
    }

    private async void PlacementScaleSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        _viewModel.PlacementScale = args.NewValue;
        await PreviewPlacementAsync();
    }

    private async void PairingCheckButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await _viewModel.RefreshPairingAsync();
            PairingStatus.Text = _viewModel.PairingAvailability == PairingAvailability.Offline
                ? "Pairing is unavailable offline. You can safely skip it."
                : "Pairing is available whenever you are ready.";
            SetMessage(null);
        }
        catch (Exception exception)
        {
            PairingStatus.Text = "Pairing is unavailable right now. You can safely skip it.";
            global::System.Diagnostics.Trace.TraceInformation("Dudu pairing check failed: {0}", exception.Message);
        }
    }

    private void SkipPairingButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.SkipPairing();
        PairingStatus.Text = "Skipped for now. You can pair later from Connection.";
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
        SyncDraftFromControls();
        _viewModel.Back();
        RefreshStep();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs args)
    {
        await MoveNextAsync();
    }

    private async void CompleteButton_Click(object sender, RoutedEventArgs args)
    {
        SyncDraftFromControls();
        try
        {
            var completed = await _viewModel.CompleteAsync();
            SetMessage(completed ? null : _viewModel.ValidationMessage);
            if (completed) _completed();
        }
        catch (OperationCanceledException)
        {
            SetMessage("Setup was paused. Your choices are still here.");
        }
        catch (Exception exception)
        {
            SetMessage("Setup could not be completed yet. Your choices are still here.");
            global::System.Diagnostics.Trace.TraceError("Dudu onboarding completion failed: {0}", exception);
        }
    }

    private async Task MoveNextAsync()
    {
        SyncDraftFromControls();
        try
        {
            var movedOrCompleted = await _viewModel.NextAsync();
            SetMessage(movedOrCompleted ? null : _viewModel.ValidationMessage);
            if (movedOrCompleted && _viewModel.IsComplete)
            {
                _completed();
                return;
            }

            RefreshStep();
        }
        catch (OperationCanceledException)
        {
            SetMessage("Setup was paused. Your choices are still here.");
        }
        catch (Exception exception)
        {
            SetMessage("Setup could not continue yet. Your choices are still here.");
            global::System.Diagnostics.Trace.TraceError("Dudu onboarding navigation failed: {0}", exception);
        }
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

        OnboardingProgress.Text = _viewModel.ProgressText;
        BackButton.IsEnabled = _viewModel.CanGoBack;
        NextButton.Visibility = _viewModel.CurrentStep == OnboardingStep.Pairing
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompleteButton.Visibility = _viewModel.CurrentStep == OnboardingStep.Pairing
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshStartupRecovery();
    }

    private void RefreshStartupRecovery()
    {
        var startup = _viewModel.StartupSettings;
        var visible = startup?.NeedsReconciliation == true;
        StartupRecoveryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StartupRecoveryMessage.Text = visible
            ? startup!.ReconciliationError ?? "Startup registration needs another try."
            : string.Empty;
    }

    private void SyncDraftFromControls()
    {
        _viewModel.RecipientName = RecipientNameBox.Text;
        _viewModel.ReducedMotion = ReducedMotionBox.IsChecked == true;
        _viewModel.QuietHoursEnabled = QuietHoursBox.IsChecked == true;
        if (TimeOnly.TryParse(QuietStartBox.Text, out var start)) _viewModel.QuietHoursStart = start;
        if (TimeOnly.TryParse(QuietEndBox.Text, out var end)) _viewModel.QuietHoursEnd = end;
        _viewModel.HydrationRemindersEnabled = HydrationBox.IsChecked == true;
        _viewModel.BreakRemindersEnabled = BreakBox.IsChecked == true;
        _viewModel.LocalNoteDailyLimit = (int)Math.Round(NoteLimitBox.Value);
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
        RecipientNameBox.Text = _viewModel.RecipientName;
        ReducedMotionBox.IsChecked = _viewModel.ReducedMotion;
        QuietHoursBox.IsChecked = _viewModel.QuietHoursEnabled;
        QuietStartBox.Text = _viewModel.QuietHoursStart.ToString("HH:mm");
        QuietEndBox.Text = _viewModel.QuietHoursEnd.ToString("HH:mm");
        HydrationBox.IsChecked = _viewModel.HydrationRemindersEnabled;
        BreakBox.IsChecked = _viewModel.BreakRemindersEnabled;
        NoteLimitBox.Value = _viewModel.LocalNoteDailyLimit;
        PlacementScaleSlider.Value = _viewModel.PlacementScale;
        HideFullscreenBox.IsChecked = _viewModel.HidePetDuringFullscreen;
        LaunchAtSignInBox.IsChecked = _viewModel.LaunchAtSignIn;
        ThemeBox.SelectedIndex = _viewModel.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };
    }

    private void SetMessage(string? message)
    {
        OnboardingValidationMessage.Text = message ?? string.Empty;
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
