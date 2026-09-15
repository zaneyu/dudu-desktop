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
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.RefreshAsync();
        SyncTaskEditor();
        RefreshFocusText();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TasksFocusViewModel.ActiveFocus)
            or nameof(TasksFocusViewModel.IsFocusActive))
        {
            RefreshFocusText();
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
            TaskDueValidation.Text = "use a date like 12/31/2026 5:00 pm";
            TaskDueValidation.Visibility = Visibility.Visible;
            SaveTaskButton.IsEnabled = false;
        }
    }

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
        var focusText = ViewModel.ActiveFocus switch
        {
            null => "nothing due now",
            { Status: FocusStatus.Running } focus => $"focus is running with {RemainingMinutes(focus)} remaining",
            { Status: FocusStatus.Paused } focus => $"focus is paused with {RemainingMinutes(focus)} remaining",
            { Status: FocusStatus.Completed } => "last focus session completed le",
            _ => "last focus session ended early",
        };
        FocusCurrent.Text = focusText;
        AutomationProperties.SetName(FocusCurrent, focusText);
    }

    private static string RemainingMinutes(FocusSnapshot focus)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(focus.Remaining.TotalMinutes));
        return $"{minutes} min left";
    }
}
