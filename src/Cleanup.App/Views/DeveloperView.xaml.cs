using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class DeveloperView : UserControl
{
    public DeveloperViewModel Model => DuplicatesDeveloperViewModels.Developer;

    public DeveloperView()
    {
        InitializeComponent();
        if (Model.ShowTools) _ = Model.LoadToolsAsync();
    }

    private async void OnRefreshTools(object sender, RoutedEventArgs e) => await Model.LoadToolsAsync();

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool Hide2(bool value) => !value;

    public static string Detail(string id) => DeveloperViewModel.Detail(id);

    private void OnProjectsTab(object sender, RoutedEventArgs e) => SelectTab(DeveloperTab.Projects);
    private void OnCachesTab(object sender, RoutedEventArgs e) => SelectTab(DeveloperTab.Caches);
    private void OnToolsTab(object sender, RoutedEventArgs e) => SelectTab(DeveloperTab.Tools);

    private void SelectTab(DeveloperTab tab)
    {
        Model.Tab = tab;
        // The buttons show the model's tab, not their own click cycle.
        ProjectsTabButton.IsChecked = Model.ShowProjects;
        CachesTabButton.IsChecked = Model.ShowCaches;
        ToolsTabButton.IsChecked = Model.ShowTools;
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FolderChip chip) Model.RemoveFolder(chip);
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

    private void OnRevealTool(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToolRow { Path: { } path }) AppServices.Reveal(path);
    }

    private void OnIgnore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ItemRow row) Model.Ignore(row);
    }

    private async void OnPruneBuildCache(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Limpar o cache de build do Docker?",
                "O Docker apaga o cache de build (docker builder prune). As próximas builds levam mais tempo. Volumes não são tocados.", "Limpar cache"))
            await Model.PruneDockerBuildCacheAsync();
    }

    private async void OnPruneImages(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Remover imagens sem uso?",
                "O Docker apaga as imagens sem nome nem uso (docker image prune). Imagens com tag e volumes não são tocados.", "Remover imagens"))
            await Model.PruneDanglingImagesAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = action, CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
