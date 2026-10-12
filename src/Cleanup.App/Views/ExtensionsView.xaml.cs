using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class ExtensionsView : UserControl
{
    public ExtensionsViewModel Model => AppsViewModels.Extensions;

    public ExtensionsView() => InitializeComponent();

    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowFirstLoad(bool hasLoaded) => hasLoaded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowNone(bool hasLoaded, int count) => hasLoaded && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RemoveLabel(int count) => $"Remover selecionados ({count})";

    /// <summary>Lists on first visit, like the macOS view's <c>.task</c>.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Model.HasLoaded && Model.LoadCommand.CanExecute(null)) Model.LoadCommand.Execute(null);
    }

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ExtensionRow { Item.RevealPath: { } path }) AppServices.Reveal(path);
    }
}
