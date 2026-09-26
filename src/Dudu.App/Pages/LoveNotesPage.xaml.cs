using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dudu.App.Pages;

public sealed partial class LoveNotesPage : Page
{
    public LoveNotesPage(LoveNotesViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public LoveNotesViewModel ViewModel { get; }

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
        SetText(LoveNotesDailyLimit, $"up to {dailyLimit} local note{(dailyLimit == 1 ? string.Empty : "s")} a day");

        var unopened = ViewModel.UnopenedRemoteNoteCount;
        SetText(LoveNotesPendingCount, unopened switch
        {
            0 => "no notes yet ah",
            1 => "1 unopened remote note",
            _ => $"{unopened} unopened remote notes",
        });
    }

    // The XAML's fixed AutomationProperties.Name ("pending notes count") overrides a
    // TextBlock's text for screen readers, so Narrator never read the count itself.
    // Keep the accessible name in step with what is on screen.
    private static void SetText(TextBlock block, string text)
    {
        block.Text = text;
        AutomationProperties.SetName(block, text);
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
