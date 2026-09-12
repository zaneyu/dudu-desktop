using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class RemindersPage : Page
{
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
        await ViewModel.RefreshAsync();
    }

    private void ReminderList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list) ViewModel.SelectedReminder = list.SelectedItem as Reminder;
    }

    private void ScheduleBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox box) return;
        ViewModel.ScheduleKind = box.SelectedIndex switch
        {
            1 => ReminderScheduleKind.Daily,
            2 => ReminderScheduleKind.SelectedWeekdays,
            3 => ReminderScheduleKind.Interval,
            _ => ReminderScheduleKind.Once,
        };
    }

    private void QuietHoursBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item
            && Enum.TryParse<QuietHoursBehavior>(item.Tag as string, out var behavior))
        {
            ViewModel.QuietHoursBehavior = behavior;
        }
    }

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsFinite(args.NewValue))
        {
            ViewModel.IntervalMinutes = (int)Math.Round(args.NewValue);
        }
    }
}
