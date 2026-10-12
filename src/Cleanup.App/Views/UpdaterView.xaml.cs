using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class UpdaterView : UserControl
{
    public UpdaterViewModel Model => AppsViewModels.Updater;

    public UpdaterView() => InitializeComponent();

    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public string Count(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private void OnIgnoreVersion(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UpdateRow row) Model.Ignore(row, wholeApp: false);
    }

    private void OnIgnoreApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UpdateRow row) Model.Ignore(row, wholeApp: true);
    }

    private void OnUnignore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IgnoredRow row) Model.Unignore(row);
    }
}
