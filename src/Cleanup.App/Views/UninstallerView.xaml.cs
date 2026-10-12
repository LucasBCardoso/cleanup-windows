using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Models;
using Cleanup.Core.Uninstaller;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

/// <summary>Desinstalador: apps on the left, the chosen app's leftovers (Desinstalar) or reset plan (Redefinir) on the right.</summary>
public sealed partial class UninstallerView : UserControl
{
    public UninstallerViewModel Model => UninstallerViewModel.Shared;

    public UninstallerView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Model.EnsureLoadedAsync();
    }

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool Not(bool value) => !value;
    public bool? IsUninstallTab(bool resetMode) => !resetMode;

    /// <summary>Registry keys have nothing to show in Explorer (static: used inside the row template).</summary>
    public static Visibility RevealVisibility(string ruleId) =>
        ruleId == LeftoverFinder.RegistryRule ? Visibility.Collapsed : Visibility.Visible;

    private async void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is AppRow row && row != Model.Chosen) await Model.ChooseAsync(row);
    }

    private void OnUninstallTab(object sender, RoutedEventArgs e) => Model.IsResetMode = false;

    private void OnResetTab(object sender, RoutedEventArgs e) => Model.IsResetMode = true;

    /// <summary>The tabs act like a segmented control: clicking the current one keeps it checked.</summary>
    private void OnTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, UninstallTab) && !Model.IsResetMode) UninstallTab.IsChecked = true;
        else if (ReferenceEquals(sender, ResetTab) && Model.IsResetMode) ResetTab.IsChecked = true;
    }

    private async void OnCheckRunning(object sender, RoutedEventArgs e) => await Model.RefreshRunningAsync();

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private void OnRevealItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ItemRow row) return;
        if (row.Item.RuleId != LeftoverFinder.RegistryRule) AppServices.Reveal(row.Item.Path);
    }

    private void OnRevealReport(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReportRow row) AppServices.Reveal(row.Item.Path);
    }

    /// <summary>Desinstalar / Redefinir, after a confirmation that says exactly what happens.</summary>
    private async void OnAct(object sender, RoutedEventArgs e)
    {
        if (Model.Chosen is not { } row || !Model.CanAct) return;
        var app = row.App;
        string title, content, primary;
        if (Model.IsResetMode)
        {
            title = $"Redefinir {app.Name}?";
            primary = "Redefinir";
            content = app.Kind == AppKind.Store
                ? "O Windows apaga os dados e ajustes do app, como em Configurações › Apps › Redefinir. Isso não pode ser desfeito."
                : $"{Model.ResetPlan.SelectedCount} item(ns), {Model.ResetPlan.SelectedSizeText}. Os arquivos vão para a Lixeira (dá para desfazer pelo Histórico) " +
                  "e as chaves do registro são salvas em .reg antes de sair.";
        }
        else
        {
            title = $"Desinstalar {app.Name}?";
            primary = "Desinstalar";
            content = app.Kind == AppKind.Store
                ? "O app e os dados dele são removidos desta conta. Isso não pode ser desfeito pelo Histórico."
                : "O desinstalador do próprio app vai abrir (ele pode pedir permissão de administrador). " +
                  (Model.Leftovers.SelectedCount > 0
                      ? $"Quando o app sair, {Model.Leftovers.SelectedCount} sobra(s) marcada(s), {Model.Leftovers.SelectedSizeText}, vão para a Lixeira; chaves do registro são salvas em .reg antes."
                      : "Nenhuma sobra está marcada.");
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (Model.IsResetMode) await Model.ResetAsync();
        else await Model.UninstallAsync();
        if (Model.Chosen is null) AppList.SelectedItem = null;
    }
}
