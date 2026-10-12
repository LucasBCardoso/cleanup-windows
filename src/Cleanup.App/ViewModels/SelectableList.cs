using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.Core.Lists;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;

namespace Cleanup.App.ViewModels;

/// <summary>One row in a list of removable items.</summary>
public sealed partial class ItemRow : ObservableObject
{
    public ScanItem Item { get; }
    private readonly Action onSelectionChanged;

    [ObservableProperty] private bool isSelected;

    public ItemRow(ScanItem item, bool selected, Action onSelectionChanged)
    {
        Item = item;
        isSelected = selected;
        this.onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => onSelectionChanged();

    public string Id => Item.Id;
    public string Name => Item.Name;
    public string Location => Item.Kind == ItemKind.RecycleBin ? "Todas as unidades" : Path.GetDirectoryName(Item.Path) ?? Item.Path;
    public string SizeText => ByteFormat.Format(Item.Size);
    public bool IsReview => Item.Risk == RiskLevel.Review;
    public bool IsAdmin => Item.RequiresAdmin;
    public string LastUsedText => Item.LastAccessedUtc is { } d ? "usado em " + d.ToLocalTime().ToString("dd/MM/yyyy") : "";
    public bool CanReveal => Item.Kind == ItemKind.Path;
}

/// <summary>A titled group of rows (one per scan task).</summary>
public sealed class ItemGroup(string title, IEnumerable<ItemRow> rows) : ObservableCollection<ItemRow>(rows)
{
    public string Title { get; } = title;
    public string SizeText => ByteFormat.Format(this.Sum(r => r.Item.Size));
}

/// <summary>
/// Selection + search for a list of items, shared by Smart Scan, Arquivos grandes and the other list modules:
/// "Selecionar tudo" covers only what the search shows, and selected-but-hidden items are counted.
/// </summary>
public abstract partial class SelectableList : ObservableObject
{
    protected List<(string Title, List<ItemRow> Rows)> AllGroups { get; private set; } = [];

    public ObservableCollection<ItemGroup> Groups { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty] private long selectedSize;
    [ObservableProperty] private int selectedCount;
    [ObservableProperty] private string countLabel = "";
    [ObservableProperty] private string? hiddenLabel;
    [ObservableProperty] private bool? selectAllState = false;
    [ObservableProperty] private string selectAllText = "Selecionar tudo";
    [ObservableProperty] private int selectedAdminCount;

    public IEnumerable<ItemRow> AllRows => AllGroups.SelectMany(g => g.Rows);
    public IEnumerable<ItemRow> ShownRows => Groups.SelectMany(g => g);
    public long TotalSize => AllRows.Sum(r => r.Item.Size);
    public int TotalCount => AllRows.Count();

    partial void OnQueryChanged(string value) => Refilter();

    protected void SetGroups(IEnumerable<(string Title, IEnumerable<ScanItem> Items)> groups, Func<ScanItem, bool> preselect)
    {
        AllGroups = groups.Select(g => (g.Title, g.Items.Select(i => new ItemRow(i, preselect(i), UpdateSelection)).ToList()))
            .Where(g => g.Item2.Count > 0).ToList();
        Refilter();
    }

    /// <summary>Drops removed items without rescanning (keeps the user's selection).</summary>
    protected void Drop(IReadOnlySet<string> ids)
    {
        AllGroups = AllGroups.Select(g => (g.Title, g.Rows.Where(r => !ids.Contains(r.Id)).ToList())).Where(g => g.Item2.Count > 0).ToList();
        Refilter();
    }

    protected void Refilter()
    {
        Groups.Clear();
        foreach (var (title, rows) in AllGroups)
        {
            var shown = rows.Where(r => SearchFilter.Matches(Query, [r.Name, r.Item.Path, title])).ToList();
            if (shown.Count > 0) Groups.Add(new ItemGroup(title, shown));
        }
        UpdateSelection();
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => AllGroups.Count == 0;

    public List<ScanItem> SelectedItems() => AllRows.Where(r => r.IsSelected).Select(r => r.Item).ToList();

    public void ToggleAll()
    {
        var shown = ShownRows.ToList();
        var ids = shown.Select(r => r.Id).ToList();
        var selected = AllRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var target = Selection.Toggled(selected, ids);
        foreach (var row in shown) row.IsSelected = target.Contains(row.Id);
    }

    protected virtual void UpdateSelection()
    {
        var all = AllRows.ToList();
        var shownIds = ShownRows.Select(r => r.Id).ToList();
        var selectedIds = all.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var state = Selection.State(selectedIds, shownIds);
        var shownSelected = shownIds.Count(selectedIds.Contains);
        SelectedSize = all.Where(r => r.IsSelected).Sum(r => r.Item.Size);
        SelectedCount = selectedIds.Count;
        SelectedAdminCount = all.Count(r => r.IsSelected && r.IsAdmin);
        CountLabel = Selection.CountLabel(shownSelected, shownIds.Count);
        HiddenLabel = Selection.HiddenLabel(selectedIds.Count - shownSelected);
        SelectAllState = state switch { SelectionState.All => true, SelectionState.None => false, _ => null };
        SelectAllText = state == SelectionState.All ? "Desmarcar tudo" : "Selecionar tudo";
        OnPropertyChanged(nameof(SelectedSizeText));
    }

    public string SelectedSizeText => ByteFormat.Format(SelectedSize);
}
