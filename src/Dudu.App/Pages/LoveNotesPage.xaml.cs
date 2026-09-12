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
        Loaded += Page_Loaded;
    }

    public LoveNotesViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshAsync();
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

    private void RemoteNoteList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView list && list.SelectedItem is RemoteEnvelope envelope)
        {
            _ = ViewModel.RevealRemoteNoteCommand.ExecuteAsync(envelope);
        }
    }
}
