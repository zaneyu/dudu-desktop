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
    private readonly Func<bool, CancellationToken, Task>? _setUserVisible;
    private bool? _placementVisibilityRequested;

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
        try
        {
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
        _viewModel.PlacementScale = args.NewValue;
        await PreviewPlacementAsync();
    }

    private async void PairingCheckButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await _viewModel.RefreshPairingAsync();
            PairingStatus.Text = _viewModel.PairingAvailability == PairingAvailability.Offline
                ? "pairing offline u can skip it"
                : "pairing ready whenever u are";
            SetMessage(null);
        }
        catch (Exception exception)
        {
            PairingStatus.Text = "pairing unavailable now u can skip it";
            global::System.Diagnostics.Trace.TraceInformation("Dudu pairing check failed: {0}", exception.Message);
        }
    }

    private void SkipPairingButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.SkipPairing();
        PairingStatus.Text = "skipped for now pair later ok";
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

                _completed();
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
            SetMessage("setup paused ur choices still here");
        }
        catch (Exception exception)
        {
            SetMessage("cannot continue setup ur choices stay");
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
        StartupRecoveryMessage.Text = visible
            ? startup!.ReconciliationError ?? "wait startup registration needs retry"
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
