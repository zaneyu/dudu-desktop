using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class RemindersPage : Page
{
    private bool _syncingEditor;

    public RemindersPage(RemindersViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
    }

    public RemindersViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.RefreshAsync();
        SyncEditorFromViewModel();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // The view model clears its selection after every save and on "new
        // reminder": mirror that into the list highlight and the unbound
        // editor controls so the form never shows one reminder while another
        // is selected.
        if (args.PropertyName == nameof(RemindersViewModel.SelectedReminder))
        {
            SyncEditorFromViewModel();
            SyncListSelectionFromViewModel();
        }
    }

    private void ReminderList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // Editor sync happens in ViewModel_PropertyChanged so programmatic
        // selection clears (save, new reminder) sync the form too.
        if (sender is not ListView list) return;

        // Replacing a row (complete, snooze, save) or reloading the list drops
        // the ListView's highlight even though the user never deselected
        // anything. Pushing that null into the view model used to leave the
        // editor pointing at nothing (so "save" made a duplicate and
        // "complete" said "select one first"). The view model keeps its own
        // selection on the fresh row; just restore the highlight from it once
        // the collection change has settled.
        if (list.SelectedItem is null && args.RemovedItems.Count > 0
            && !args.RemovedItems.Any(removed => ViewModel.Reminders.Any(item => ReferenceEquals(item, removed))))
        {
            DispatcherQueue.TryEnqueue(SyncListSelectionFromViewModel);
            return;
        }

        ViewModel.SelectedReminder = list.SelectedItem as Reminder;
    }

    private void SyncListSelectionFromViewModel()
    {
        var selected = ViewModel.SelectedReminder;
        var match = selected is null
            ? null
            : ViewModel.Reminders.FirstOrDefault(item => item.Id == selected.Id);
        if (!ReferenceEquals(ReminderList.SelectedItem, match))
        {
            ReminderList.SelectedItem = match;
        }
    }

    private void ScheduleBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingEditor) return;
        if (sender is not ComboBox box) return;
        ViewModel.ScheduleKind = box.SelectedIndex switch
        {
            1 => ReminderScheduleKind.Daily,
            2 => ReminderScheduleKind.SelectedWeekdays,
            3 => ReminderScheduleKind.Interval,
            _ => ReminderScheduleKind.Once,
        };
        // An interval reminder ignores the local time, so a half-typed time
        // must not keep "save reminder" disabled (and vice versa).
        ValidateLocalTime();
    }

    private void QuietHoursBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingEditor) return;
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item
            && Enum.TryParse<QuietHoursBehavior>(item.Tag as string, out var behavior))
        {
            ViewModel.QuietHoursBehavior = behavior;
        }
    }

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingEditor) return;
        if (double.IsFinite(args.NewValue))
        {
            ViewModel.IntervalMinutes = (int)Math.Round(args.NewValue);
        }
    }

    private void LocalTimeBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_syncingEditor) return;
        ValidateLocalTime();
    }

    private void ValidateLocalTime()
    {
        string? error = null;
        if (ViewModel.UsesLocalTime)
        {
            if (string.IsNullOrWhiteSpace(LocalTimeBox.Text))
            {
                error = "aiyo enter a time or use 09:00";
            }
            else if (TimeOnly.TryParse(LocalTimeBox.Text, out var localTime))
            {
                ViewModel.LocalTime = localTime;
            }
            else
            {
                error = "oh no use a time like 09:00";
            }
        }

        SetValidationText(error ?? string.Empty);
        RemindersLocalTimeValidation.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        SaveReminderButton.IsEnabled = error is null;
    }

    private void WeekdayBox_Changed(object sender, RoutedEventArgs args)
    {
        if (_syncingEditor || sender is not CheckBox box
            || !Enum.TryParse<DayOfWeek>(box.Tag as string, out var day)) return;

        var selected = new HashSet<DayOfWeek>(ViewModel.SelectedWeekdays);
        if (box.IsChecked == true) selected.Add(day);
        else selected.Remove(day);
        ViewModel.SelectedWeekdays = selected;
    }

    private void SyncEditorFromViewModel()
    {
        _syncingEditor = true;
        try
        {
            ScheduleBox.SelectedIndex = ViewModel.ScheduleKind switch
            {
                ReminderScheduleKind.Daily => 1,
                ReminderScheduleKind.SelectedWeekdays => 2,
                ReminderScheduleKind.Interval => 3,
                _ => 0,
            };
            LocalTimeBox.Text = ViewModel.LocalTime.ToString("HH:mm");
            IntervalBox.Value = ViewModel.IntervalMinutes;
            QuietHoursBox.SelectedIndex = ViewModel.QuietHoursBehavior == QuietHoursBehavior.DeliverImmediately ? 0 : 1;
            SundayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Sunday);
            MondayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Monday);
            TuesdayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Tuesday);
            WednesdayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Wednesday);
            ThursdayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Thursday);
            FridayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Friday);
            SaturdayBox.IsChecked = ViewModel.SelectedWeekdays.Contains(DayOfWeek.Saturday);
            SetValidationText(string.Empty);
            RemindersLocalTimeValidation.Visibility = Visibility.Collapsed;
            SaveReminderButton.IsEnabled = true;
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    // A fixed AutomationProperties.Name ("local time validation") overrides the
    // TextBlock's text for screen readers, so the actual problem was never read out.
    private void SetValidationText(string text)
    {
        RemindersLocalTimeValidation.Text = text;
        AutomationProperties.SetName(RemindersLocalTimeValidation, text.Length == 0 ? "local time validation" : text);
    }
}
