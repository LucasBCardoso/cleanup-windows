using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class HistoryDialog : ContentDialog
{
    public HistoryViewModel Model => AppViewModels.History;

    public HistoryDialog()
    {
        InitializeComponent();
        Opened += async (_, _) => await Model.ReloadAsync();
    }

    public Visibility Empty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryRow row) await Model.UndoAsync(row);
    }
}
