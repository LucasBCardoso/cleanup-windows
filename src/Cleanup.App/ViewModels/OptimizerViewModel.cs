using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Admin;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Maintenance;
using Cleanup.Core.Models;
using Cleanup.Core.Optimization;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>
/// "Otimização total": clean safe junk (system junk too), free memory, flush DNS, clear the thumbnail cache and remove
/// startup entries whose program is certainly gone — at most one UAC prompt. Uses Smart Scan's results (scanning first
/// when there are none) and respects what the user unticked there.
/// </summary>
public sealed partial class OptimizerViewModel : ObservableObject
{
    private readonly AppServices services = AppServices.Shared;
    private readonly ScanViewModel scan;
    private readonly OptimizationViewModel optimization;
    private readonly MaintenanceViewModel maintenance;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRunning), nameof(ButtonText))] [NotifyCanExecuteChangedFor(nameof(OptimizeAllCommand))]
    private string? step;
    [ObservableProperty] private Banner? banner;

    public OptimizerViewModel(ScanViewModel scan, OptimizationViewModel optimization, MaintenanceViewModel maintenance)
    {
        this.scan = scan;
        this.optimization = optimization;
        this.maintenance = maintenance;
        scan.PropertyChanged += OnOtherChanged;
        maintenance.PropertyChanged += OnOtherChanged;
    }

    private void OnOtherChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScanViewModel.IsScanning) or nameof(ScanViewModel.IsCleaning) or nameof(ScanViewModel.Phase)
            or nameof(MaintenanceViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(IsBlocked));
            OptimizeAllCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsRunning => Step is not null;
    public string ButtonText => IsRunning ? "Otimizando…" : "Otimizar tudo";

    /// <summary>Smart Scan is scanning or cleaning (or Manutenção runs): optimizing now would clean the same items twice.</summary>
    public bool IsBlocked => scan.IsScanning || scan.IsCleaning || maintenance.IsRunning;

    private bool CanOptimize() => !IsRunning && !IsBlocked;

    [RelayCommand(CanExecute = nameof(CanOptimize))]
    private async Task OptimizeAllAsync()
    {
        if (IsRunning || IsBlocked) return; // a double click must not start a second run
        Banner = null;
        Step = "Procurando o que otimizar…";
        try
        {
            if (!scan.HasResults) await scan.ScanAsync();
            if (!scan.HasResults) throw new InvalidOperationException("o Smart Scan não terminou");
            scan.IsOptimizing = true;
            try { await RunAsync(); }
            finally { scan.IsOptimizing = false; }
        }
        catch (Exception e)
        {
            Banner = new Banner(BannerKind.Error, "A otimização não pôde terminar.", [e.Message]);
        }
        finally
        {
            Step = null;
        }
    }

    private async Task RunAsync()
    {
        await optimization.LoadAsync();
        var environment = services.Environment;
        var plan = OneClickOptimizer.Plan(scan.SelectedItems(), environment, optimization.Items, MaintenanceTask.All, Drives.IsUnverifiable);
        Step = plan.NeedsAdmin ? "Otimizando (o Windows vai pedir permissão uma vez)…" : "Otimizando…";
        var policy = services.Policy();
        var remover = services.Remover;
        var report = await Task.Run(() =>
        {
            var controller = StartupController.Current(() => new Cleaner(policy, remover));
            return OneClickOptimizer.RunAsync(plan, new Cleaner(policy, remover), AdminPathPolicy.For(environment), MaintenanceRunner.Current,
                controller.Remove);
        });
        await services.RecordAsync(report.Recordable, report.Admin.Removed, "Otimização total");
        scan.RemoveCleaned(report.Clean.Removed.Concat(report.Admin.Removed).Select(i => i.Id).ToHashSet());
        Banner = BannerFor(report);
        Step = "Atualizando…";
        await optimization.LoadAsync();
    }

    private static string TaskName(string id) => MaintenanceTask.All.FirstOrDefault(t => t.Id == id)?.Name ?? id;

    public static string Summary(OptimizationReport report)
    {
        var parts = new List<string> { $"{ByteFormat.Format(report.FreedBytes)} liberados" };
        if (report.TasksDone.Contains(MaintenanceTask.FreeRam)) parts.Add("memória liberada");
        if (report.TasksDone.Contains(MaintenanceTask.FlushDns)) parts.Add("DNS renovado");
        if (report.TasksDone.Contains(MaintenanceTask.ClearThumbnails)) parts.Add("miniaturas limpas");
        if (report.StartupRemoved > 0) parts.Add($"{report.StartupRemoved} item(ns) de inicialização quebrado(s) removido(s)");
        return string.Join(" · ", parts);
    }

    public static Banner BannerFor(OptimizationReport report)
    {
        var failures = report.Clean.Failed.Concat(report.Admin.Failed).Select(f => $"{f.Item.Name}: {f.Reason}")
            .Concat(report.TasksFailed.Select(id => $"{TaskName(id)}: falhou"))
            .Concat(report.StartupFailures)
            .ToList();
        var summary = Summary(report);
        if (report.AdminCancelled)
        {
            return new Banner(BannerKind.Info,
                summary + ". Sem a permissão de administrador, os arquivos do sistema, a memória e o DNS ficaram de fora.", failures);
        }
        if (failures.Count > 0) return new Banner(BannerKind.Warning, $"{summary}; {failures.Count} item(ns) falharam.", failures);
        // Only the Recycle Bin part can come back; system items are deleted for good.
        var undo = report.Recordable.Removed.Count == 0 ? "" : " O que foi para a Lixeira dá para desfazer em Hoje › Histórico.";
        var permanent = report.Admin.Removed.Count == 0 ? "" : " Itens do sistema foram apagados definitivamente.";
        return new Banner(BannerKind.Success, summary + "." + undo + permanent);
    }
}
