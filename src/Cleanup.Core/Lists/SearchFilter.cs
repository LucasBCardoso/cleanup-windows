using System.Globalization;
using System.Text;

namespace Cleanup.Core.Lists;

/// <summary>List search: every word of the query must appear in one of the fields, ignoring case and accents.</summary>
public static class SearchFilter
{
    public static bool Matches(string query, IEnumerable<string> fields)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Fold).ToList();
        if (words.Count == 0) return true;
        var haystack = fields.Select(Fold).ToList();
        return words.All(word => haystack.Any(field => field.Contains(word, StringComparison.Ordinal)));
    }

    internal static string Fold(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

public enum SelectionState
{
    None,
    Some,
    All,
}

/// <summary>How much of a list is selected. Only listed ids count: items hidden by a search or in another tab don't.</summary>
public static class Selection
{
    public static SelectionState State(IReadOnlySet<string> selected, IReadOnlyList<string> ids)
    {
        var count = ids.Count(selected.Contains);
        return ids.Count == 0 || count == 0 ? SelectionState.None : count == ids.Count ? SelectionState.All : SelectionState.Some;
    }

    /// <summary>Selects every listed item, or deselects them all when they already are. Other selected ids are kept.</summary>
    public static HashSet<string> Toggled(IReadOnlySet<string> selected, IReadOnlyList<string> ids)
    {
        var result = new HashSet<string>(selected);
        if (State(selected, ids) == SelectionState.All) result.ExceptWith(ids);
        else result.UnionWith(ids);
        return result;
    }

    /// <summary>"1 de 2 selecionados" — the plural follows the total, as in the macOS bar.</summary>
    public static string CountLabel(int selectedCount, int total) => $"{selectedCount} de {total} selecionado{(total == 1 ? "" : "s")}";

    /// <summary>"+ 2 fora da busca" when the search hides selected items (actions include them), else null.</summary>
    public static string? HiddenLabel(int hiddenSelected) => hiddenSelected > 0 ? $"+ {hiddenSelected} fora da busca" : null;
}
