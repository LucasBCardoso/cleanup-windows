using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Optimization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Cleanup.App.Views;

public sealed partial class OptimizationView : UserControl
{
    public OptimizationViewModel Model => SystemViewModels.Optimization;
    public OptimizerViewModel Optimizer => SystemViewModels.Optimizer;

    /// <summary>The process a menu was opened on, captured when it opened: refreshes can't swap it for another.</summary>
    private ProcessSample? menuSample;

    public OptimizationView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            UpdateSampling();
            if (!Model.HasLoaded) await Model.LoadAsync();
        };
        Unloaded += (_, _) => Model.StopSampling();
    }

    public bool Not(bool value) => !value;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool IsEmpty(int count) => count == 0;
    public string FooterText(string? step) => step ?? "Limpa lixo seguro, libera memória, renova o DNS, limpa miniaturas e remove itens de inicialização quebrados.";

    private void SelectTab(OptimizationTab tab)
    {
        Model.Tab = tab;
        StartupTab.IsChecked = Model.IsStartupTab;
        ConsumersTab.IsChecked = Model.IsConsumersTab;
        UpdateSampling();
    }

    private void UpdateSampling()
    {
        if (Model.IsConsumersTab) Model.StartSampling(); else Model.StopSampling();
    }

    private void OnStartupTab(object sender, RoutedEventArgs e) => SelectTab(OptimizationTab.Startup);
    private void OnConsumersTab(object sender, RoutedEventArgs e) => SelectTab(OptimizationTab.Consumers);
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.LoadAsync();
    private void OnOpenStartupSettings(object sender, RoutedEventArgs e) => AppServices.Open("ms-settings:startupapps");
    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;
    private void OnOptimizerBannerDismissed(object? sender, EventArgs e) => Optimizer.Banner = null;

    private static StartupRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as StartupRow;

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { Item: { } item } row) await Model.SetEnabledAsync(row, item.IsDisabled);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Item: { } item } row) return;
        var message = item.Kind == StartupKind.StartupFolder
            ? "O atalho vai para a Lixeira e o app deixa de abrir ao iniciar sessão. Dá para desfazer em Hoje › Histórico."
            : "O item sai do Registro e o app deixa de abrir ao iniciar sessão. Uma cópia fica guardada em “Removidos”, para restaurar quando quiser.";
        if (await ConfirmAsync($"Remover {Model.Describe(row)}?", message, "Remover")) await Model.RemoveAsync(row);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsBackup: true } row) await Model.RestoreAsync(row);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Item: { } item }) return;
        AppServices.Reveal(item.Kind == StartupKind.StartupFolder ? item.Location : item.Program ?? item.Location);
    }

    private void OnProcessMenuOpening(object? sender, object e)
    {
        menuSample = ((sender as FlyoutBase)?.Target?.DataContext as ProcessRow)?.Sample;
        Model.Paused = true;
    }

    private void OnProcessMenuClosed(object? sender, object e) => Model.Paused = false;

    private async void OnQuit(object sender, RoutedEventArgs e)
    {
        if (menuSample is { } sample) await Model.TerminateAsync(sample, force: false);
    }

    private async void OnForceQuit(object sender, RoutedEventArgs e)
    {
        if (menuSample is not { } sample) return;
        if (await ConfirmAsync($"Forçar o encerramento de {sample.Name}?", "Alterações não salvas nesse processo serão perdidas.", "Forçar encerramento"))
            await Model.TerminateAsync(sample, force: true);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action, CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
