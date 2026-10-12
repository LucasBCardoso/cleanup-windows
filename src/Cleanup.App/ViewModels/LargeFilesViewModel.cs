using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Scanning;

namespace Cleanup.App.ViewModels;

/// <summary>Arquivos grandes: big files in the profile folder, optionally unused for N days. Nothing is pre-selected.</summary>
public sealed partial class LargeFilesViewModel : SelectableList
{
    private readonly AppServices services = AppServices.Shared;

    public static IReadOnlyList<(string Label, long Bytes)> SizeOptions { get; } =
        [("50 MB", 50_000_000), ("100 MB", 100_000_000), ("500 MB", 500_000_000), ("1 GB", 1_000_000_000), ("5 GB", 5_000_000_000)];

    public static IReadOnlyList<(string Label, int? Days)> AgeOptions { get; } =
        [("Qualquer data", null), ("1 mês", 30), ("3 meses", 90), ("6 meses", 180), ("1 ano", 365)];

    public IReadOnlyList<string> SizeLabels { get; } = SizeOptions.Select(o => o.Label).ToList();
    public IReadOnlyList<string> AgeLabels { get; } = AgeOptions.Select(o => o.Label).ToList();

    [ObservableProperty] private int sizeIndex = 1;
    [ObservableProperty] private int ageIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResults))] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    private bool isScanning;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))] private bool isCleaning;
    [ObservableProperty] private bool hasScanned;
    [ObservableProperty] private Banner? banner;

    public bool HasResults => HasScanned && !IsScanning;

    private bool CanScan() => !IsScanning && !IsCleaning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        Banner = null;
        try
        {
            var task = new LargeOldFilesTask(SizeOptions[Math.Clamp(SizeIndex, 0, SizeOptions.Count - 1)].Bytes,
                AgeOptions[Math.Clamp(AgeIndex, 0, AgeOptions.Count - 1)].Days);
            var report = await Task.Run(() => task.Scan(services.Environment, services.Policy(), CancellationToken.None));
            // Grouped by top-level folder of the profile ("Downloads", "Videos"…), like Finder's sidebar.
            var home = services.Environment.Home;
            var groups = report.Items.GroupBy(i => TopFolder(i.Path, home))
                .Select(g => (g.Key, (IEnumerable<ScanItem>)g.OrderByDescending(i => i.Size).ToList()))
                .OrderByDescending(g => g.Item2.Sum(i => i.Size));
            SetGroups(groups, _ => false);
            HasScanned = true;
            if (report.Items.Count == 0) Banner = new Banner(BannerKind.Info, "Nenhum arquivo com esse tamanho" + (AgeIndex > 0 ? " e idade." : "."));
            else if (AgeIndex > 0) Banner = new Banner(BannerKind.Info, "O Windows nem sempre registra quando um arquivo foi aberto; usamos também a última alteração.");
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsScanning = false; }
    }

    private static string TopFolder(string path, string home)
    {
        if (!path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return Path.GetPathRoot(path) ?? path;
        var rest = path[(home.Length + 1)..];
        var cut = rest.IndexOf('\\');
        return cut < 0 ? "Pasta pessoal" : rest[..cut];
    }

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        CleanCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsScanning && !IsCleaning && SelectedSize > 0;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        IsCleaning = true;
        try
        {
            var result = await services.CleanAsync(SelectedItems());
            await services.RecordAsync(result, null, "Arquivos grandes");
            Drop(result.Removed.Select(i => i.Id).ToHashSet());
            Banner = Banner.FromClean(result);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsCleaning = false; }
    }

    public void Ignore(ItemRow row)
    {
        services.Ignore(row.Item.Path);
        Drop(new HashSet<string> { row.Id });
    }
}
