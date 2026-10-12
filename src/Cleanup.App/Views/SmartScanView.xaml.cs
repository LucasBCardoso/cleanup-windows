using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class SmartScanView : UserControl
{
    public ScanViewModel Model => AppViewModels.Scan;

    public SmartScanView() => InitializeComponent();

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Summary(long size, int count) => $"{ByteFormat.Format(size)} encontrados · {count} {(count == 1 ? "item" : "itens")}";
    public string AdminNote(int count) => $"{count} do sistema: pede permissão de administrador uma vez";

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
