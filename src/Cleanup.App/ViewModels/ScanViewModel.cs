using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Scanning;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

public enum ScanPhase { Idle, Scanning, Results }

/// <summary>Smart Scan: rule-driven junk scan, safe items pre-selected, user items to the Recycle Bin, system items via UAC.</summary>
public sealed partial class ScanViewModel : SelectableList
{
    private readonly AppServices services = AppServices.Shared;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsScanning), nameof(IsIdle), nameof(HasResults))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(CleanSafeCommand), nameof(ScanCommand))]
    private ScanPhase phase = ScanPhase.Idle;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string currentTask = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(CleanSafeCommand), nameof(ScanCommand))]
    private bool isCleaning;
    /// <summary>Set while Otimização total works on these results: Smart Scan's own buttons wait.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(CleanSafeCommand), nameof(ScanCommand))]
    private bool isOptimizing;
    [ObservableProperty] private Banner? banner;
    [ObservableProperty] private IReadOnlyList<string> skipped = [];

    public ScanViewModel()
    {
        if (services.StartupError is { } error) banner = new Banner(BannerKind.Error, error);
    }

    public bool IsScanning => Phase == ScanPhase.Scanning;
    public bool IsIdle => Phase == ScanPhase.Idle;
    public bool HasResults => Phase == ScanPhase.Results;
    public bool IsBusy => IsCleaning || IsOptimizing;

    /// <summary>What "Limpar agora" on Hoje takes: selected, safe, and needing no administrator.</summary>
    public IReadOnlyList<ScanItem> SafeSelected =>
        AllRows.Where(r => r.IsSelected && r.Item.Risk == RiskLevel.Safe && !r.Item.RequiresAdmin).Select(r => r.Item).ToList();

    public long SafeSelectedSize => SafeSelected.Sum(i => i.Size);

    /// <summary>The three biggest groups of what "Limpar agora" would free (for the Hoje hero).</summary>
    public IReadOnlyList<(string Title, long Bytes)> TopSafeGroups =>
        AllGroups.Select(g => (g.Title, g.Rows.Where(r => r.IsSelected && r.Item.Risk == RiskLevel.Safe && !r.Item.RequiresAdmin).Sum(r => r.Item.Size)))
            .Where(g => g.Item2 > 0).OrderByDescending(g => g.Item2).Take(3).ToList();

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        OnPropertyChanged(nameof(SafeSelectedSize));
        OnPropertyChanged(nameof(TopSafeGroups));
        CleanCommand.NotifyCanExecuteChanged();
        CleanSafeCommand.NotifyCanExecuteChanged();
    }

    private bool CanScan() => !IsScanning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        if (IsScanning || IsBusy) return;
        Phase = ScanPhase.Scanning;
        Progress = 0;
        CurrentTask = "";
        Banner = null;
        var progress = new Progress<ScanProgress>(p => { if (IsScanning) { Progress = p.Fraction * 100; CurrentTask = p.LastTaskTitle; } });
        var outcomes = await Task.Run(async () =>
        {
            var tasks = ScanEngine.SmartScanTasks(services.Rules, RunningProcesses.Names(), [new RecycleBinTask()]);
            return await new ScanEngine(services.Environment, services.Policy()).RunAsync(tasks, progress);
        });
        var reports = outcomes.Select(o => o.Report).OfType<ScanReport>().ToList();
        SetGroups(reports.Where(r => r.Items.Count > 0).Select(r => (r.Title, (IEnumerable<ScanItem>)r.Items)), i => i.Risk == RiskLevel.Safe);
        Skipped = reports.SelectMany(r => r.Skipped).ToList();
        var failures = outcomes.OfType<ScanOutcome.Failure>().Select(f => $"{f.Title}: {f.Message}").ToList();
        Banner = services.IgnoreListError is { } ignoreError ? new Banner(BannerKind.Error, ignoreError)
            : failures.Count > 0 ? new Banner(BannerKind.Error, $"{failures.Count} verificação(ões) falharam.", failures)
            : Banner.Skipped(Skipped);
        Phase = ScanPhase.Results;
        ScanCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsBusy && !IsScanning && SelectedSize > 0;
    private bool CanCleanSafe() => !IsBusy && !IsScanning && SafeSelectedSize > 0;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private Task CleanAsync() => CleanItemsAsync(SelectedItems());

    /// <summary>"Limpar agora" on Hoje: never review items, never a surprise UAC prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanSafe))]
    private Task CleanSafeAsync() => CleanItemsAsync(SafeSelected);

    private async Task CleanItemsAsync(IReadOnlyList<ScanItem> chosen)
    {
        if (IsBusy || chosen.Count == 0) return; // a double click must not start a second clean
        IsCleaning = true;
        try
        {
            // The Recycle Bin part first, recorded right away: a dismissed or failed UAC prompt (or closing the app while
            // it's open) must not cost the user their other items or the way to undo them.
            var result = await services.CleanAsync(chosen.Where(i => !i.RequiresAdmin).ToList());
            await services.RecordAsync(result, null, "Smart Scan");
            Drop(result.Removed.Select(i => i.Id).ToHashSet());
            var admin = await services.CleanAsAdminAsync(chosen.Where(i => i.RequiresAdmin).ToList());
            if (admin.Removed.Count > 0)
            {
                await services.RecordAsync(result with { Removed = [], RecycleBinLocations = new Dictionary<string, string>() }, admin.Removed,
                    "Smart Scan (sistema)");
            }
            Drop(admin.Removed.Select(i => i.Id).ToHashSet());
            Banner = Banner.FromClean(result, admin);
        }
        catch (Exception e)
        {
            Banner = new Banner(BannerKind.Error, e.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    /// <summary>Otimização total removed these: take them out of the results without rescanning.</summary>
    public void RemoveCleaned(IReadOnlySet<string> ids) => Drop(ids);

    public void Ignore(ItemRow row)
    {
        try
        {
            services.Ignore(row.Item.Path);
            Drop(new HashSet<string> { row.Id });
            Banner = new Banner(BannerKind.Info, $"{row.Name} não será mais sugerido.");
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
    }
}
