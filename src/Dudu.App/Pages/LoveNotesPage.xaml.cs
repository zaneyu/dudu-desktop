using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class LoveNotesPage : Page
{
    public LoveNotesPage(LoveNotesViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += Page_Loaded;
    }

    public LoveNotesViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshAsync();
        RefreshCountText();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LoveNotesViewModel.UnopenedRemoteNoteCount)
            or nameof(LoveNotesViewModel.DailyLocalNoteLimit))
        {
            RefreshCountText();
        }
    }

    private void RefreshCountText()
    {
        var dailyLimit = ViewModel.DailyLocalNoteLimit;
        LoveNotesDailyLimit.Text = $"dudu can show up to {dailyLimit} local note{(dailyLimit == 1 ? string.Empty : "s")} each day";

        var unopened = ViewModel.UnopenedRemoteNoteCount;
        LoveNotesPendingCount.Text = unopened switch
        {
            0 => "no unopened remote notes",
            1 => "1 unopened remote note",
            _ => $"{unopened} unopened remote notes",
        };
    }

    private void LocalNoteList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            var note = list.SelectedItem as LocalLoveNote;
            ViewModel.SelectedNote = note;
            ViewModel.DraftText = note?.Text ?? string.Empty;
            ViewModel.DraftEnabled = note?.Enabled ?? true;
        }
    }

}
