using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.Core.Lists;
using Cleanup.Core.Models;

namespace Cleanup.App.ViewModels;

/// <summary>A row of a list whose items aren't scan items (updates, extensions). Some rows are shown but can't be selected.</summary>
public abstract partial class SelectableRow : ObservableObject
{
    [ObservableProperty] private bool isSelected;

    internal Action? SelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public abstract string Id { get; }
    public abstract bool CanSelect { get; }
    public abstract IEnumerable<string> SearchFields { get; }
    public virtual long Bytes => 0;
}

/// <summary>A titled group of rows, with a count/size on the right and an optional note under the title.</summary>
public sealed class RowGroup(string title, string trailing, string? note, IEnumerable<SelectableRow> rows) : ObservableCollection<SelectableRow>(rows)
{
    public string Title { get; } = title;
    public string Trailing { get; } = trailing;
    public string Note { get; } = note ?? "";
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// <see cref="SelectableList"/> for custom rows, so the same <c>SelectionBar</c> works: search filters the rows,
/// "Selecionar tudo" covers only shown rows that <see cref="InSelectAll"/> admits, and hidden selections are counted.
/// </summary>
public abstract class RowList<TRow> : SelectableList where TRow : SelectableRow
{
    private List<(string Title, string? Note, Func<IReadOnlyList<TRow>, string> Trailing, List<TRow> Rows)> rowGroups = [];

    public ObservableCollection<RowGroup> ShownGroups { get; } = [];
    public ObservableCollection<SelectableRow> ShownRowsFlat { get; } = [];

    protected RowList()
    {
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Query)) RefilterRows(); };
    }

    public IEnumerable<TRow> AllItems => rowGroups.SelectMany(g => g.Rows);
    public IEnumerable<TRow> ShownItems => ShownRowsFlat.OfType<TRow>();
    public int RowCount => rowGroups.Sum(g => g.Rows.Count);
    public bool HasRows => RowCount > 0;
    public bool HasSelectableRows => AllItems.Any(r => r.CanSelect);

    /// <summary>Whether "Selecionar tudo" takes this row (Store updates, for one, need a deliberate tick).</summary>
    protected virtual bool InSelectAll(TRow row) => row.CanSelect;

    protected void SetRows(IEnumerable<(string Title, string? Note, Func<IReadOnlyList<TRow>, string> Trailing, IEnumerable<TRow> Rows)> groups)
    {
        rowGroups = groups.Select(g => (g.Title, g.Note, g.Trailing, g.Rows.ToList())).Where(g => g.Item4.Count > 0).ToList();
        foreach (var row in AllItems) row.SelectionChanged = UpdateSelection;
        RefilterRows();
    }

    /// <summary>Takes rows out (installed, removed, ignored) and keeps everyone else's selection.</summary>
    protected void DropRows(IReadOnlySet<string> ids)
    {
        rowGroups = rowGroups.Select(g => (g.Title, g.Note, g.Trailing, g.Rows.Where(r => !ids.Contains(r.Id)).ToList())).Where(g => g.Item4.Count > 0).ToList();
        RefilterRows();
    }

    protected void RefilterRows()
    {
        ShownGroups.Clear();
        ShownRowsFlat.Clear();
        foreach (var (title, note, trailing, rows) in rowGroups)
        {
            var shown = rows.Where(r => SearchFilter.Matches(Query, [.. r.SearchFields, title])).ToList();
            if (shown.Count == 0) continue;
            ShownGroups.Add(new RowGroup(title, trailing(rows), note, shown));
            foreach (var row in shown) ShownRowsFlat.Add(row);
        }
        UpdateSelection();
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasSelectableRows));
    }

    public List<TRow> SelectedRows() => AllItems.Where(r => r.IsSelected && r.CanSelect).ToList();

    public override void ToggleAll()
    {
        var shown = ShownItems.Where(InSelectAll).ToList();
        var ids = shown.Select(r => r.Id).ToList();
        var selected = AllItems.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var target = Selection.Toggled(selected, ids);
        foreach (var row in shown) row.IsSelected = target.Contains(row.Id);
    }

    protected override void UpdateSelection()
    {
        var all = AllItems.ToList();
        var shownIds = ShownItems.Where(InSelectAll).Select(r => r.Id).ToList();
        var selectedIds = all.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Id).ToHashSet();
        var state = Selection.State(selectedIds, shownIds);
        var shownSelected = ShownItems.Count(r => r.IsSelected && r.CanSelect);
        SelectedSize = all.Where(r => r.IsSelected && r.CanSelect).Sum(r => r.Bytes);
        SelectedCount = selectedIds.Count;
        CountLabel = Selection.CountLabel(shownSelected, ShownItems.Count(r => r.CanSelect));
        HiddenLabel = Selection.HiddenLabel(selectedIds.Count - shownSelected);
        SelectAllState = state switch { SelectionState.All => true, SelectionState.None => false, _ => null };
        SelectAllText = state == SelectionState.All ? "Desmarcar tudo" : "Selecionar tudo";
        OnPropertyChanged(nameof(SelectedSizeText));
        SelectionDidChange();
    }

    protected virtual void SelectionDidChange() { }

    protected static string SizeOf(IEnumerable<SelectableRow> rows) => ByteFormat.Format(rows.Sum(r => r.Bytes));
}
