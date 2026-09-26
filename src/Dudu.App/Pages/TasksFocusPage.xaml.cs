using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class TasksFocusPage : Page
{
    private bool _syncingTaskEditor;
    private bool _applyingDueFromBox;
    private DispatcherQueueTimer? _focusTimer;

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
        _focusTimer?.Stop();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.AttachFocusExpiry();
        StartFocusTimer();

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
        switch (args.PropertyName)
        {
            case nameof(TasksFocusViewModel.ActiveFocus) or nameof(TasksFocusViewModel.IsFocusActive):
                RefreshFocusText();
                break;
            case nameof(TasksFocusViewModel.DueUtc) when !_applyingDueFromBox:
                // After a save (or "new task") the view model clears the due
                // date; without this the unbound box kept the old text while
                // the value was null, so the next task saved without that date.
                SyncTaskEditor();
                break;
            case nameof(TasksFocusViewModel.SelectedTask):
                SyncTaskEditor();
                if (!Equals(ActiveTaskList.SelectedItem, ViewModel.SelectedTask))
                {
                    ActiveTaskList.SelectedItem = ViewModel.SelectedTask;
                }

                break;
        }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // The editor resyncs from ViewModel_PropertyChanged, which also covers
        // programmatic clears (after save, complete, delete or "new task").
        if (sender is ListView list)
        {
            ViewModel.SelectTask(list.SelectedItem as TaskItem);
        }
    }

    private void TaskDueBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_syncingTaskEditor || sender is not TextBox box) return;
        _applyingDueFromBox = true;
        try
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                ViewModel.DueUtc = null;
                TaskDueValidation.Text = string.Empty;
                TaskDueValidation.Visibility = Visibility.Collapsed;
                SaveTaskButton.IsEnabled = true;
            }
            else if (DateTimeOffset.TryParse(box.Text, out var due))
            {
                ViewModel.DueUtc = due;
                TaskDueValidation.Text = string.Empty;
                TaskDueValidation.Visibility = Visibility.Collapsed;
                SaveTaskButton.IsEnabled = true;
            }
            else
            {
                TaskDueValidation.Text = $"use a date like {DateHintExample()}";
                TaskDueValidation.Visibility = Visibility.Visible;
                SaveTaskButton.IsEnabled = false;
            }
        }
        finally
        {
            _applyingDueFromBox = false;
        }
    }

    /// <summary>A focus snapshot's remaining time is fixed when it is read, so
    /// a light timer re-renders the countdown while the page is visible. It
    /// only re-reads view-model state; no storage access per tick.</summary>
    private void StartFocusTimer()
    {
        if (_focusTimer is null)
        {
            _focusTimer = DispatcherQueue.CreateTimer();
            _focusTimer.Interval = TimeSpan.FromSeconds(15);
            _focusTimer.Tick += FocusTimer_Tick;
        }

        _focusTimer.Start();
    }

    private void FocusTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            RefreshFocusText();
        }
        catch (Exception exception)
        {
            // A throw from a timer tick is unhandled and would repeat; stop instead.
            sender.Stop();
            global::System.Diagnostics.Trace.TraceError("Dudu focus countdown tick failed: {0}", exception);
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
            TaskDueValidation.Text = string.Empty;
            TaskDueValidation.Visibility = Visibility.Collapsed;
            SaveTaskButton.IsEnabled = true;
        }
        finally
        {
            _syncingTaskEditor = false;
        }
    }

    private void RefreshFocusText()
    {
        var remaining = ViewModel.ActiveFocusRemaining ?? TimeSpan.Zero;
        var focusText = ViewModel.ActiveFocus switch
        {
            null => "no focus running",
            { Status: FocusStatus.Running } => $"focus is running with {RemainingMinutes(remaining)} remaining",
            { Status: FocusStatus.Paused } => $"focus is paused with {RemainingMinutes(remaining)} remaining",
            { Status: FocusStatus.Completed } => "last focus session completed le",
            _ => "last focus session ended early",
        };
        if (string.Equals(FocusCurrent.Text, focusText, StringComparison.Ordinal)) return;
        FocusCurrent.Text = focusText;
        AutomationProperties.SetName(FocusCurrent, focusText);
    }

    private static string RemainingMinutes(TimeSpan remaining)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"{minutes} min left";
    }
}
