using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Cleanup.App.Views;

public sealed partial class DuplicatesView : UserControl
{
    public DuplicatesViewModel Model => DuplicatesDeveloperViewModels.Duplicates;

    public DuplicatesView() => InitializeComponent();

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "Fica" / "Melhor" tag (rows are rebuilt whenever the keep rule changes).</summary>
    public static Visibility KeeperVisibility(string id) =>
        DuplicatesDeveloperViewModels.Duplicates.Identical.Keepers.Contains(id) || DuplicatesDeveloperViewModels.Duplicates.Similar.Keepers.Contains(id)
            ? Visibility.Visible : Visibility.Collapsed;

    public static string DateText(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("dd/MM/yyyy") : "";

    private void OnIdenticalTab(object sender, RoutedEventArgs e) => SelectTab(DuplicatesTab.Identical);
    private void OnSimilarTab(object sender, RoutedEventArgs e) => SelectTab(DuplicatesTab.Similar);

    private void SelectTab(DuplicatesTab tab)
    {
        Model.Tab = tab;
        // The buttons show the model's tab, not their own click cycle.
        IdenticalTabButton.IsChecked = Model.IsIdenticalTab;
        SimilarTabButton.IsChecked = Model.IsSimilarTab;
    }

    private void OnFolderToggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FolderChip chip) Model.FolderToggled(chip);
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        try { await Model.AddFolderAsync(); }
        catch (Exception ex) { Model.Banner = new Banner(BannerKind.Error, "Não foi possível escolher a pasta.", [ex.Message]); }
    }

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ItemRow row) AppServices.Reveal(row.Item.Path);
    }

    private void OnKeepBest(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ItemGroup group) Model.KeepBest(group);
    }

    /// <summary>A downsampled preview, decoded when a tile appears (containers are recycled, so the image is reset).</summary>
    private async void OnPhotoContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ItemRow row || FindImage(args.ItemContainer.ContentTemplateRoot) is not { } image) return;
        image.Source = null;
        var path = row.Item.Path;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 300, DecodePixelType = DecodePixelType.Logical };
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            if (args.ItemContainer.Content == row) image.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    private static Image? FindImage(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is Image image) return image;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindImage(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
