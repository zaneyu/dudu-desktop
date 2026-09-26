using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class TasksFocusPage : Page
{
    private bool _syncingTaskEditor;
    private bool _applyingDueFromView;
    private DispatcherTimer? _focusCountdownTimer;

    public TasksFocusPage(TasksFocusViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public TasksFocusViewModel ViewModel { get; }

    // SettingsWindow caches this page and swaps it in and out of the content
    // frame, so Loaded/Unloaded fire on every visit. The view-model
    // subscription is live only while the page is in the tree; the page's own
    // Loaded/Unloaded handlers stay attached so revisits still refresh.
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return; // a re-load already won the out-of-order race
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.DetachFocusExpiry();
        _focusCountdownTimer?.Stop();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.AttachFocusExpiry();
        // The focus status line used to be computed once per snapshot, so a
        // running session sat at "25 min left" for its whole length. Tick it.
        if (_focusCountdownTimer is null)
        {
            _focusCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _focusCountdownTimer.Tick += FocusCountdownTimer_Tick;
        }
        _focusCountdownTimer.Start();

        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu tasks/focus refresh failed: {0}", exception);
        }

        SyncTaskEditor();
        RefreshFocusText();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TasksFocusViewModel.ActiveFocus)
            or nameof(TasksFocusViewModel.IsFocusActive)
            or nameof(TasksFocusViewModel.ActiveFocusText))
        {
            RefreshFocusText();
        }
        else if (args.PropertyName == nameof(TasksFocusViewModel.DueUtc) && !_applyingDueFromView)
        {
            // The due box is not x:Bound: when the view model clears or loads the
            // editor (after save, complete, delete) mirror it, or the box kept the
            // previous task's due date while the next save silently dropped it.
            SyncTaskEditor();
        }
    }

    /// <summary>Called when the hosting window closes: Unloaded is not guaranteed for a
    /// closed window's content, and the UI thread (and so this timer) outlives it.</summary>
    public void StopFocusCountdown() => _focusCountdownTimer?.Stop();

    private void FocusCountdownTimer_Tick(object? sender, object args)
    {
        if (ViewModel.ActiveFocus is { Status: FocusStatus.Running })
        {
            ViewModel.RefreshFocusCountdown();
        }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            ViewModel.SelectTask(list.SelectedItem as TaskItem);
            SyncTaskEditor();
        }
    }

    private void TaskDueBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_syncingTaskEditor || sender is not TextBox box) return;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            SetDueFromView(null);
            SetDueValidationText(string.Empty);
            TaskDueValidation.Visibility = Visibility.Collapsed;
            SaveTaskButton.IsEnabled = true;
        }
        else if (DateTimeOffset.TryParse(box.Text, out var due))
        {
            SetDueFromView(due);
            SetDueValidationText(string.Empty);
            TaskDueValidation.Visibility = Visibility.Collapsed;
            SaveTaskButton.IsEnabled = true;
        }
        else
        {
            SetDueValidationText($"use a date like {DateHintExample()}");
            TaskDueValidation.Visibility = Visibility.Visible;
            SaveTaskButton.IsEnabled = false;
        }
    }

    private void SetDueFromView(DateTimeOffset? due)
    {
        _applyingDueFromView = true;
        try
        {
            ViewModel.DueUtc = due;
        }
        finally
        {
            _applyingDueFromView = false;
        }
    }

    /// <summary>Locale-correct date example, rendered with the same general pattern the
    /// free-text parser accepts, so following the hint always parses.</summary>
    private static string DateHintExample() =>
        new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("g");

    private void FocusPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag as string, out var minutes))
        {
            ViewModel.SelectedDurationMinutes = minutes;
        }
    }

    private void CustomDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsFinite(args.NewValue))
        {
            ViewModel.CustomDurationMinutes = (int)Math.Round(args.NewValue);
        }
    }

    private void SyncTaskEditor()
    {
        _syncingTaskEditor = true;
        try
        {
            TaskDueBox.Text = ViewModel.DueUtc?.ToLocalTime().ToString("g") ?? string.Empty;
            SetDueValidationText(string.Empty);
            TaskDueValidation.Visibility = Visibility.Collapsed;
            SaveTaskButton.IsEnabled = true;
        }
        finally
        {
            _syncingTaskEditor = false;
        }
    }

    // A fixed AutomationProperties.Name ("due date validation") overrides the
    // TextBlock's text for screen readers, so the date hint was never read out.
    private void SetDueValidationText(string text)
    {
        TaskDueValidation.Text = text;
        AutomationProperties.SetName(TaskDueValidation, text.Length == 0 ? "due date validation" : text);
    }

    private void RefreshFocusText()
    {
        var focusText = ViewModel.ActiveFocusText;
        if (string.Equals(FocusCurrent.Text, focusText, StringComparison.Ordinal)) return;
        FocusCurrent.Text = focusText;
        AutomationProperties.SetName(FocusCurrent, focusText);
    }
}
