using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class LargeFilesView : UserControl
{
    public LargeFilesViewModel Model => AppViewModels.LargeFiles;

    public LargeFilesView() => InitializeComponent();

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowEmpty(int count, bool scanning) => count == 0 && !scanning ? Visibility.Visible : Visibility.Collapsed;

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ItemRow row) AppServices.Reveal(row.Item.Path);
    }

    private void OnIgnore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ItemRow row) Model.Ignore(row);
    }
}
