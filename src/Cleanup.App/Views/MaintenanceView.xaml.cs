using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class MaintenanceView : UserControl
{
    public MaintenanceViewModel Model => SystemViewModels.Maintenance;

    public MaintenanceView() => InitializeComponent();

    public bool Not(bool value) => !value;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        Model.ToggleAll();
        // The checkbox shows the list's state, not its own click cycle.
        SelectAll.IsChecked = Model.SelectAllState;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MaintenanceTaskRow row) await Model.RunAsync(row);
    }

    private async void OnRunSelected(object sender, RoutedEventArgs e) => await Model.RunSelectedAsync();

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;
}
