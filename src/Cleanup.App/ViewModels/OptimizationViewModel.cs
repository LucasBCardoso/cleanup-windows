using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Lists;
using Cleanup.Core.Models;
using Cleanup.Core.Optimization;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>Sistema's view models (Otimização, Otimização total, Manutenção): they outlive their views.</summary>
public static class SystemViewModels
{
    public static OptimizationViewModel Optimization { get; } = new();
    public static MaintenanceViewModel Maintenance { get; } = new();
    public static OptimizerViewModel Optimizer { get; } = new(AppViewModels.Scan, Optimization, Maintenance);
}

public enum OptimizationTab { Startup, Consumers }

/// <summary>One row of the startup list: an entry, or a removed Run value that can be restored.</summary>
public sealed partial class StartupRow : ObservableObject
{
    public StartupItem? Item { get; }
    public StartupBackup? Backup { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowActions), nameof(ShowRestore))] private bool isBusy;
    [ObservableProperty] private bool actionsEnabled = true;

    public StartupRow(StartupItem item)
    {
        Item = item;
        CanReveal = item.Program is { } p && Path.IsPathFullyQualified(p) && File.Exists(p)
                    || item.Kind == StartupKind.StartupFolder && File.Exists(item.Location);
    }

    public StartupRow(StartupBackup backup) => Backup = backup;

    public string Id => Item?.Id ?? "backup:" + Backup!.Id;
    public string Title => Item?.Name ?? Backup!.Name;
    public string Subtitle => Item is { } i ? i.BrokenReason ?? i.Command ?? i.Location : Backup!.Command;
    public string Detail => Item is { } i
        ? i.Publisher is { } publisher ? $"{publisher} · {i.SourceLabel}" : i.SourceLabel
        : $"Removido em {Backup!.Date.LocalDateTime:dd/MM/yyyy HH:mm} · HKCU\\{Backup.KeyPath}";
    public string Glyph => Item?.Kind switch
    {
        StartupKind.Service => "",
        StartupKind.ScheduledTask => "",
        StartupKind.StartupFolder => "",
        null => "",
        _ => "",
    };

    public bool IsBroken => Item?.IsBroken == true;
    public bool IsDisabled => Item?.IsDisabled == true;
    public bool IsOnce => Item?.Kind == StartupKind.RunOnce;
    public bool IsBackup => Backup is not null;
    public bool CanToggle => Item?.CanToggle == true;
    public bool CanManage => Item?.IsManageable == true;
    public bool CanReveal { get; }
    public string ToggleText => IsDisabled ? "Ativar" : "Desativar";
    public bool ShowActions => CanManage && !IsBusy;
    public bool ShowRestore => IsBackup && !IsBusy;
    public bool ShowToggle => CanToggle;
    public bool ShowReveal => !CanManage && !IsBackup && CanReveal;

    public bool Matches(string query) => SearchFilter.Matches(query,
        [Title, Item?.Publisher ?? "", Item?.Command ?? Backup?.Command ?? "", Item?.Program ?? ""]);
}

public sealed class StartupRowGroup(string title, string trailing, IEnumerable<StartupRow> rows) : ObservableCollection<StartupRow>(rows)
{
    public string Title { get; } = title;
    public string Trailing { get; } = trailing;
}

/// <summary>One line of a "Consumo" list, updated in place every refresh.</summary>
public sealed partial class ProcessRow(ProcessMetric metric, ProcessSample sample) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Name), nameof(PidText), nameof(ValueText), nameof(CanQuit))]
    private ProcessSample sample = sample;

    public string Name => Sample.Name;
    public string PidText => $"PID {Sample.Pid}";
    public string ValueText => metric == ProcessMetric.Cpu ? ProcessSampler.FormatCpu(Sample.Cpu) : ByteFormat.Format(Sample.MemoryBytes);
    public bool CanQuit => !ProcessTerminator.IsProtected(Sample.Name);
}

/// <summary>
/// Otimização: startup entries (enable, disable, remove the user's own; the rest read-only) and the heaviest
/// processes of the user, refreshed live, with quit and force quit.
/// </summary>
public sealed partial class OptimizationViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    public const int TopCount = 10;

    private readonly AppServices services = AppServices.Shared;
    private Task? loading;
    private CancellationTokenSource? sampling;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsStartupTab), nameof(IsConsumersTab), nameof(Subtitle))]
    private OptimizationTab tab = OptimizationTab.Startup;
    [ObservableProperty] private string query = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading))] private bool isLoading;
    [ObservableProperty] private Banner? banner;
    [ObservableProperty] private string? busyId;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading))] private int itemCount;
    /// <summary>A process menu is open: the lists hold still so the row under the pointer stays the same process.</summary>
    public bool Paused { get; set; }

    public IReadOnlyList<StartupItem> Items { get; private set; } = [];
    public IReadOnlyList<StartupBackup> Backups { get; private set; } = [];
    public ObservableCollection<StartupRowGroup> Groups { get; } = [];
    public ObservableCollection<ProcessRow> TopCpu { get; } = [];
    public ObservableCollection<ProcessRow> TopMemory { get; } = [];

    public bool IsStartupTab => Tab == OptimizationTab.Startup;
    public bool IsConsumersTab => Tab == OptimizationTab.Consumers;
    public bool ShowLoading => IsLoading && ItemCount == 0;

    public string Subtitle
    {
        get
        {
            if (Tab == OptimizationTab.Consumers) return "Atualiza a cada 3 segundos · somente processos do seu usuário";
            if (!HasLoaded) return "Registro, pasta Inicializar, tarefas agendadas e serviços.";
            var broken = Items.Count(i => i.IsBroken);
            return $"{Items.Count} {(Items.Count == 1 ? "item" : "itens")}" + (broken > 0 ? $" · {broken} quebrado(s)" : "");
        }
    }

    partial void OnQueryChanged(string value) => Refilter();
    partial void OnBusyIdChanged(string? value)
    {
        foreach (var row in Groups.SelectMany(g => g)) row.ActionsEnabled = value is null;
    }

    /// <summary>Reads the startup entries; a second call while reading waits for the same read.</summary>
    public Task LoadAsync() => loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            (Items, Backups) = await Task.Run(() =>
            {
                var locations = StartupLocations.Current();
                try
                {
                    var items = new StartupInventory(locations).Load();
                    return (items, new StartupBackupStore(StartupBackupStore.DefaultFolder).Load());
                }
                finally { locations.MachineHive?.Dispose(); }
            });
            HasLoaded = true;
            Refilter();
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, "Não foi possível ler os itens de inicialização.", [e.Message]); }
        finally
        {
            IsLoading = false;
            loading = null;
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    private void Refilter()
    {
        Groups.Clear();
        foreach (var group in Enum.GetValues<StartupGroup>())
        {
            var rows = Items.Where(i => StartupGroups.Of(i) == group).Select(i => new StartupRow(i)).Where(r => r.Matches(Query)).ToList();
            if (rows.Count == 0) continue;
            var trailing = group.IsManageable() ? $"{rows.Count}" : $"somente leitura · {rows.Count}";
            Groups.Add(new StartupRowGroup(group.Title(), trailing, rows));
        }
        var backups = Backups.Select(b => new StartupRow(b)).Where(r => r.Matches(Query)).ToList();
        if (backups.Count > 0) Groups.Add(new StartupRowGroup("Removidos (dá para restaurar)", $"{backups.Count}", backups));
        foreach (var row in Groups.SelectMany(g => g)) row.ActionsEnabled = BusyId is null;
        ItemCount = Items.Count + Backups.Count;
    }

    private static string NameOf(StartupItem item) => item.Publisher is { } p && !p.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
        ? $"{item.Name} ({p})" : item.Name;

    public string Describe(StartupRow row) => row.Item is { } item ? NameOf(item) : row.Title;

    public Task SetEnabledAsync(StartupRow row, bool enabled) => PerformAsync(row,
        $"{Describe(row)} {(enabled ? "ativado" : "desativado")}. Vale a partir do próximo início de sessão.",
        controller => controller.SetEnabled(row.Item!, enabled));

    /// <summary>
    /// A Startup-folder file goes to the Recycle Bin and is recorded (undo in Hoje › Histórico); a Run value is backed
    /// up first and listed under "Removidos", where it can be restored.
    /// </summary>
    public Task RemoveAsync(StartupRow row)
    {
        var item = row.Item!;
        var folder = item.Kind == StartupKind.StartupFolder;
        var success = folder
            ? $"{Describe(row)} movido para a Lixeira. Dá para desfazer em Hoje › Histórico."
            : $"{Describe(row)} removido. Dá para restaurar em “Removidos”, no fim da lista.";
        return PerformAsync(row, success, controller =>
        {
            var removal = controller.Remove(item);
            if (!removal.Removed) throw new InvalidOperationException(removal.Error ?? "não foi possível remover");
            return removal.Recycled;
        });
    }

    public Task RestoreAsync(StartupRow row) => PerformAsync(row,
        $"{row.Title} restaurado. Volta a abrir no próximo início de sessão.", controller => controller.Restore(row.Backup!));

    private Task PerformAsync(StartupRow row, string success, Action<StartupController> action) =>
        PerformAsync(row, success, controller => { action(controller); return null; });

    private async Task PerformAsync(StartupRow row, string success, Func<StartupController, CleanResult?> action)
    {
        if (BusyId is not null || IsLoading || SystemViewModels.Optimizer.IsRunning) return;
        BusyId = row.Id;
        row.IsBusy = true;
        try
        {
            var policy = services.Policy();
            var recycled = await Task.Run(() => action(StartupController.Current(() => new Cleaner(policy, services.Remover))));
            if (recycled is not null) await services.RecordAsync(recycled, null, "Otimização");
            Banner = new Banner(BannerKind.Success, success);
        }
        catch (Exception e)
        {
            Banner = new Banner(BannerKind.Error, $"Não foi possível alterar {Describe(row)}.", [e.Message]);
        }
        finally
        {
            row.IsBusy = false;
            BusyId = null;
        }
        await LoadAsync();
    }

    // MARK: Consumo

    public void StartSampling()
    {
        if (sampling is not null) return;
        sampling = new CancellationTokenSource();
        _ = SampleAsync(sampling.Token);
    }

    public void StopSampling()
    {
        sampling?.Cancel();
        sampling = null;
    }

    private async Task SampleAsync(CancellationToken cancellation)
    {
        var snapshotter = new ProcessSnapshotter();
        IReadOnlyList<ProcessSnapshot> previous = [];
        var clock = Stopwatch.StartNew();
        var first = true;
        while (!cancellation.IsCancellationRequested)
        {
            IReadOnlyList<ProcessSnapshot> current;
            try { current = await Task.Run(snapshotter.OwnProcesses, cancellation); }
            catch (OperationCanceledException) { return; }
            var elapsed = clock.Elapsed;
            clock.Restart();
            var samples = ProcessSampler.Compute(previous, current, elapsed, Environment.ProcessorCount);
            previous = current;
            if (!Paused && !cancellation.IsCancellationRequested)
            {
                if (!first) Fill(TopCpu, ProcessSampler.Top(samples, ProcessMetric.Cpu, TopCount), ProcessMetric.Cpu);
                Fill(TopMemory, ProcessSampler.Top(samples, ProcessMetric.Memory, TopCount), ProcessMetric.Memory);
            }
            // The first CPU reading needs a second one to compare with: take it soon.
            try { await Task.Delay(first ? TimeSpan.FromSeconds(1) : RefreshInterval, cancellation); }
            catch (OperationCanceledException) { return; }
            first = false;
        }
    }

    private static void Fill(ObservableCollection<ProcessRow> rows, IReadOnlyList<ProcessSample> top, ProcessMetric metric)
    {
        for (var i = 0; i < top.Count; i++)
        {
            if (i < rows.Count) rows[i].Sample = top[i];
            else rows.Add(new ProcessRow(metric, top[i]));
        }
        while (rows.Count > top.Count) rows.RemoveAt(rows.Count - 1);
    }

    public async Task TerminateAsync(ProcessSample sample, bool force)
    {
        try
        {
            await Task.Run(() => WindowsProcessControl.Terminator().Terminate(sample, force));
            Banner = new Banner(BannerKind.Success, force ? $"{sample.Name} foi forçado a encerrar." : $"Pedido de encerramento enviado a {sample.Name}.");
        }
        catch (Exception e) when (e is TerminateException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Banner = new Banner(BannerKind.Error, $"Não foi possível encerrar {sample.Name}.", [e.Message]);
        }
    }
}
