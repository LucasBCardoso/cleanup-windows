using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.SpaceLens;

namespace Cleanup.App.ViewModels;

public sealed partial class LensRow : ObservableObject
{
    private readonly long max;
    private readonly Action? onSelectionChanged;

    /// <param name="blockedReason">Why this item can't be moved to the Recycle Bin (safety policy, link), or null.</param>
    public LensRow(DirectoryEntry entry, long max, string? blockedReason = null, Action? onSelectionChanged = null)
    {
        Entry = entry;
        this.max = max;
        BlockedReason = blockedReason;
        this.onSelectionChanged = onSelectionChanged;
    }

    [ObservableProperty] private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => onSelectionChanged?.Invoke();

    public string? BlockedReason { get; }
    public bool CanSelect => BlockedReason is null;
    public string? SelectNote => BlockedReason is null ? null : "Não pode ir para a Lixeira: " + BlockedReason;

    public DirectoryEntry Entry { get; }
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
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RecycleSelectedCommand))] private int selectedCount;
    [ObservableProperty] private string selectedText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RecycleSelectedCommand))] private bool isRecycling;
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
        UpdateSelection();
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
            var policy = services.Policy();
            foreach (var e in entries) Rows.Add(new LensRow(e, max, BlockedReason(e, policy), UpdateSelection));
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

    /// <summary>Why an item can't be selected: links, and whatever the safety policy refuses (the same check as at clean time).</summary>
    private static string? BlockedReason(DirectoryEntry entry, Cleanup.Core.Safety.SafetyPolicy policy) =>
        entry.IsLink ? "é um atalho para outra pasta"
        : policy.Verdict(entry.Path) is Cleanup.Core.Safety.SafetyVerdict.Denied denied ? denied.Reason
        : null;

    public IReadOnlyList<LensRow> SelectedRows => Rows.Where(r => r.IsSelected).ToList();

    private void UpdateSelection()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        SelectedCount = selected.Count;
        SelectedText = selected.Count == 0 ? ""
            : $"{selected.Count} {(selected.Count == 1 ? "item selecionado" : "itens selecionados")} · {ByteFormat.Format(selected.Sum(r => r.Entry.Size))}";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Rows) row.IsSelected = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Rows.Where(r => r.CanSelect)) row.IsSelected = true;
    }

    private bool CanRecycleSelected() => SelectedCount > 0 && !IsRecycling;

    /// <summary>The view confirms first (it owns the dialog), then calls this.</summary>
    [RelayCommand(CanExecute = nameof(CanRecycleSelected))]
    private Task RecycleSelectedAsync() => RecycleAsync(SelectedRows);

    public Task RecycleAsync(LensRow row) => RecycleAsync([row]);

    /// <summary>
    /// To the Recycle Bin through the shared cleaner (the safety policy is checked again for every item), recorded in the
    /// history so it can be undone, and taken off the measured tree so the sizes above stay right.
    /// </summary>
    public async Task RecycleAsync(IReadOnlyList<LensRow> rows)
    {
        if (IsRecycling || rows.Count == 0) return;
        IsRecycling = true;
        try
        {
            var items = rows.Select(r => new ScanItem
            {
                Path = r.Entry.Path, Size = r.Entry.Size, RuleId = "space-lens", Category = ScanCategory.LargeOldFiles, Risk = RiskLevel.Review,
            }).ToList();
            var result = await services.CleanAsync(items);
            await services.RecordAsync(result, null, "Space Lens");
            var removed = result.Removed.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(r => removed.Contains(r.Entry.Path)).ToList())
            {
                tree?.Removed(row.Entry.Path, row.Entry.Size);
                Rows.Remove(row);
            }
            UpdateSelection();
            TotalText = $"{ByteFormat.Format(Rows.Sum(r => r.Entry.Size))} em {Rows.Count} {(Rows.Count == 1 ? "item" : "itens")}";
            Banner = Banner.FromClean(result);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsRecycling = false; }
    }
}
