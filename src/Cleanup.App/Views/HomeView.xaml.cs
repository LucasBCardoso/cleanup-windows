using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Models;
using Cleanup.Core.SpaceLens;
using Cleanup.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class HomeView : UserControl
{
    public ScanViewModel Scan => AppViewModels.Scan;
    public HistoryViewModel History => AppViewModels.History;
    public HealthViewModel Health => AppViewModels.Health!;

    public HomeView()
    {
        InitializeComponent();
        Greeting.Text = GreetingFor(DateTime.Now.Hour, Environment.UserName);
        Loaded += async (_, _) =>
        {
            await RefreshTilesAsync();
            await History.ReloadAsync();
            await RefreshAppTilesAsync();
        };
        UpdaterViewModel.UpdatesFoundChanged += () => DispatcherQueue.TryEnqueue(ShowUpdates);
        Scan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanViewModel.IsCleaning) && !Scan.IsCleaning) _ = RefreshTilesAsync();
        };
    }

    /// <summary>"Bom dia, Lucas" — the same greeting rules as the macOS HomeSummary.</summary>
    public string GreetingFor(int hour, string name)
    {
        var part = hour < 5 ? "Boa noite" : hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite";
        var first = name.Split([' ', '.', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? part : $"{part}, {char.ToUpper(first[0]) + first[1..]}";
    }

    private async Task RefreshTilesAsync()
    {
        var root = Path.GetPathRoot(AppServices.Shared.Environment.Home) ?? "C:\\";
        var (disk, bin) = await Task.Run(() => (DiskSpace.Of(root), RecycleBin.Query()));
        if (disk is not null)
        {
            DiskFree.Text = ByteFormat.Format(disk.Free);
            DiskLine.Text = $"livres em {root.TrimEnd('\\')} · {ByteFormat.Format(disk.Used)} usados de {ByteFormat.Format(disk.Total)}";
            DiskBar.Value = disk.Total == 0 ? 0 : (double)disk.Used / disk.Total;
        }
        BinSize.Text = ByteFormat.Format(bin.Size);
        BinLine.Text = bin.Items == 0 ? "A Lixeira está vazia." : $"{bin.Items} {(bin.Items == 1 ? "item" : "itens")} em todas as unidades. O Smart Scan pode esvaziá-la.";
    }

    private bool appTilesStarted;

    /// <summary>Atualizações (a read-only winget listing) and Apps esquecidos (6 months unused), once per launch.</summary>
    private async Task RefreshAppTilesAsync()
    {
        if (appTilesStarted) return;
        appTilesStarted = true;
        ShowUpdates();
        var updater = AppsViewModels.Updater;
        if (UpdaterViewModel.UpdatesFound is null && updater.CheckCommand.CanExecute(null)) updater.CheckCommand.Execute(null);
        try
        {
            var forgotten = await UninstallerViewModel.Shared.ForgottenCountAsync();
            ForgottenCount.Text = forgotten.ToString();
            ForgottenLine.Text = forgotten switch
            {
                0 => "Nenhum app parado há mais de 6 meses.",
                1 => "1 app não é usado há mais de 6 meses.",
                _ => $"{forgotten} apps não são usados há mais de 6 meses.",
            };
        }
        catch (Exception e)
        {
            ForgottenCount.Text = "?";
            ForgottenLine.Text = "Não foi possível ler os apps: " + e.Message;
        }
    }

    private void ShowUpdates()
    {
        if (UpdaterViewModel.UpdatesFound is not { } count) return;
        UpdatesCount.Text = count.ToString();
        UpdatesLine.Text = count switch
        {
            0 => "Tudo atualizado.",
            1 => "1 app tem versão nova.",
            _ => $"{count} apps têm versão nova.",
        };
    }

    private void OnUpdates(object sender, RoutedEventArgs e) => App.Window?.Navigate(Module.Updater);
    private void OnUninstaller(object sender, RoutedEventArgs e) => App.Window?.Navigate(Module.Uninstaller);

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public bool Not(bool value) => !value;
    public string Bytes(long value) => ByteFormat.Format(value);
    public string Percent(double value) => $"{(int)value}%";
    public string Checking(string task) => string.IsNullOrEmpty(task) ? "Verificando…" : $"Verificando {task}…";
    public string CleanLabel(bool cleaning) => cleaning ? "Limpando…" : "Limpar agora";

    public string Subtitle(ScanPhase phase, long safe) => phase switch
    {
        ScanPhase.Results when safe == 0 => "Nada para limpar agora. Seu PC está em dia.",
        ScanPhase.Results => "Seu PC está bem. Veja o que dá para melhorar hoje.",
        ScanPhase.Scanning => "Procurando o que dá para limpar…",
        _ => "Faça uma verificação para ver o que dá para melhorar.",
    };

    public IReadOnlyList<HeroGroup> TopGroups(long _) => Scan.TopSafeGroups.Select(g => new HeroGroup(g.Title, ByteFormat.Format(g.Bytes))).ToList();

    public string SkippedText(IReadOnlyList<string> skipped) =>
        skipped.Count == 0 ? "" : $"{skipped.Count} {(skipped.Count == 1 ? "local do sistema não foi lido" : "locais do sistema não foram lidos")}; a limpeza do sistema pede permissão de administrador uma vez.";

    public Visibility ShowUndo(HistoryRow? row) => row is null ? Visibility.Collapsed : Visibility.Visible;
    public string UndoTitle(HistoryRow? row) => row is null ? "" : $"{row.Record.Source}: {ByteFormat.Format(row.Record.TotalBytes)} em {row.When}";
    public string UndoStatus(HistoryRow? row) => row?.Status ?? "";
    public string BannerText(Banner? banner) => banner?.Message ?? "";

    private void OnDetails(object sender, RoutedEventArgs e) => App.Window?.Navigate(Module.SmartScan);
    private void OnSpaceLens(object sender, RoutedEventArgs e) => App.Window?.Navigate(Module.SpaceLens);
    private void OnHealth(object sender, RoutedEventArgs e) => App.ShowHealthPanel();

    public string HealthHeadline(string summary) => summary;
    public string HealthLine(string cpu, string pressure, string battery) =>
        string.Join(" · ", new[] { cpu == "—" ? "" : $"CPU {cpu}", pressure.Length > 0 ? $"memória {pressure}" : "", battery.Length > 0 ? $"bateria {battery}" : "" }.Where(s => s.Length > 0));
    private void OnOptimization(object sender, RoutedEventArgs e) => App.Window?.Navigate(Module.Optimization);
    private void OnOpenBin(object sender, RoutedEventArgs e) => AppServices.Open("shell:RecycleBinFolder");
    private async void OnHistory(object sender, RoutedEventArgs e) { if (App.Window is { } w) await w.ShowHistoryAsync(); }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (History.LastUndoable is { } row) await History.UndoAsync(row);
        await RefreshTilesAsync();
    }
}


