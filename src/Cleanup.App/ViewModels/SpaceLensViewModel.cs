using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.SpaceLens;

namespace Cleanup.App.ViewModels;

public sealed class LensRow(DirectoryEntry entry, long max)
{
    public DirectoryEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string SizeText => Entry.IsLink ? "link" : (Entry.IsPartial ? "≥ " : "") + ByteFormat.Format(Entry.Size);
    /// <summary>Why a size is a lower bound (shown as a tooltip).</summary>
    public string? SizeNote => Entry.IsLink ? "Atalho para outra pasta: não é seguido nem contado." : Entry.IsPartial ? "Parte desta pasta só pode ser lida como administrador; o tamanho real é maior." : null;
    public double Fraction => max <= 0 ? 0 : Math.Max(0.004, (double)Entry.Size / max);
    public string Glyph => Entry.IsLink ? "\uE71B" : Entry.IsDirectory ? "\uE8B7" : "\uE8A5";
    public bool CanOpen => Entry.IsDirectory;
}

public sealed class DriveRow(DriveInfo drive, DiskSpace disk)
{
    public string Root { get; } = drive.Name;
    public string Label { get; } = $"{drive.Name.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : drive.VolumeLabel)}".Trim();
    public DiskSpace Disk { get; } = disk;
    public string Summary => $"{ByteFormat.Format(Disk.Free)} livres de {ByteFormat.Format(Disk.Total)}";
    public double UsedFraction => Disk.Total == 0 ? 0 : (double)Disk.Used / Disk.Total;
    public override string ToString() => Label;
}

/// <summary>Space Lens: what is using space, folder by folder; links are shown, never followed.</summary>
public sealed partial class SpaceLensViewModel : ObservableObject
{
    private readonly AppServices services = AppServices.Shared;
    private CancellationTokenSource? measuring;

    public ObservableCollection<LensRow> Rows { get; } = [];
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<string> Crumbs { get; } = [];

    [ObservableProperty] private string currentPath = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UpCommand))] private bool isMeasuring;
    [ObservableProperty] private long filesSeen;
    private SpaceTree? tree;

    /// <summary>Raised once a folder's rows are listed (the view scrolls back to the top).</summary>
    public event Action? Listed;
    [ObservableProperty] private string totalText = "";
    [ObservableProperty] private DriveRow? selectedDrive;
    [ObservableProperty] private Banner? banner;

    public SpaceLensViewModel()
    {
        LoadDrives();
    }

    public void LoadDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            if (DiskSpace.Of(d.Name) is { } disk) Drives.Add(new DriveRow(d, disk));
        }
        loadingDrives = true;
        SelectedDrive = Drives.FirstOrDefault(d => d.Root.StartsWith(Path.GetPathRoot(services.Environment.Home) ?? "C:", StringComparison.OrdinalIgnoreCase))
                        ?? Drives.FirstOrDefault();
        loadingDrives = false;
    }

    public Task OpenHomeAsync() => OpenAsync(services.Environment.Home);

    private bool loadingDrives;

    partial void OnSelectedDriveChanged(DriveRow? value)
    {
        if (!loadingDrives && value is not null && !string.Equals(Path.GetPathRoot(CurrentPath), value.Root, StringComparison.OrdinalIgnoreCase))
            _ = OpenAsync(value.Root);
    }

    [RelayCommand]
    public async Task OpenAsync(string path)
    {
        measuring?.Cancel();
        var cts = measuring = new CancellationTokenSource();
        CurrentPath = path;
        UpdateCrumbs();
        Rows.Clear();
        Banner = null;
        IsMeasuring = true;

        try
        {
            // One walk measures every folder below; opening a subfolder afterwards needs no new walk. Going up reuses
            // what was already measured below.
            if (tree is null || !tree.Covers(path))
            {
                var progress = new Progress<long>(n => { if (!cts.IsCancellationRequested) FilesSeen = n; });
                FilesSeen = 0;
                tree = await SpaceTree.BuildAsync(path, reuse: tree, filesSeen: progress, cancellation: cts.Token);
            }
            var current = tree;
            var entries = await Task.Run(() => current.Children(path), cts.Token);
            if (cts.IsCancellationRequested) return;
            var max = entries.FirstOrDefault()?.Size ?? 0;
            foreach (var e in entries) Rows.Add(new LensRow(e, max));
            Listed?.Invoke();
            var measured = entries.Sum(e => e.Size);
            TotalText = $"{ByteFormat.Format(measured)} em {entries.Count} {(entries.Count == 1 ? "item" : "itens")}";
            // At a drive root, say how much of the used space no folder accounts for (what needs administrator to read,
            // NTFS metadata, shadow copies), instead of letting the list look complete.
            if (string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase) && DiskSpace.Of(path) is { } disk
                && disk.Used - measured > disk.Total / 200)
            {
                TotalText += $" · {ByteFormat.Format(disk.Used - measured)} sem identificar (só o administrador lê)";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Banner = new Banner(BannerKind.Warning, $"Não foi possível abrir {path}: {e.Message}");
        }
        finally
        {
            if (measuring == cts) IsMeasuring = false;
        }
    }

    private void UpdateCrumbs()
    {
        Crumbs.Clear();
        var root = Path.GetPathRoot(CurrentPath) ?? CurrentPath;
        Crumbs.Add(root);
        var acc = root;
        foreach (var part in CurrentPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Path.Combine(acc, part);
            Crumbs.Add(acc);
        }
    }

    /// <summary>"Atualizar": the disk strip and the current folder, measured again.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        var path = CurrentPath;
        tree = null; // measure again from scratch
        var drive = SelectedDrive?.Root;
        loadingDrives = true;
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            if (DiskSpace.Of(d.Name) is { } disk) Drives.Add(new DriveRow(d, disk));
        }
        SelectedDrive = Drives.FirstOrDefault(d => d.Root == drive) ?? Drives.FirstOrDefault();
        loadingDrives = false;
        return OpenAsync(string.IsNullOrEmpty(path) ? services.Environment.Home : path);
    }

    private bool CanGoUp() => Path.GetDirectoryName(CurrentPath) is not null;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task UpAsync() => Path.GetDirectoryName(CurrentPath) is { } parent ? OpenAsync(parent) : Task.CompletedTask;

    public async Task RecycleAsync(LensRow row)
    {
        var item = new ScanItem
        {
            Path = row.Entry.Path, Size = row.Entry.Size, RuleId = "space-lens", Category = ScanCategory.LargeOldFiles, Risk = RiskLevel.Review,
        };
        var result = await services.CleanAsync([item]);
        await services.RecordAsync(result, null, "Space Lens");
        if (result.Removed.Count > 0) Rows.Remove(row);
        Banner = Banner.FromClean(result);
    }
}
