using Cleanup.Core.Safety;

namespace Cleanup.Core.Duplicates;

/// <summary>Which copy of a duplicate group to keep; every other copy is selected for removal.</summary>
public enum KeepRule
{
    Oldest,
    Newest,
    OutsideDownloads,
}

/// <summary>The keep rule logic, the same as the Swift <c>KeepRule</c> (shared vectors: <c>keep-rule.json</c>).</summary>
public static class KeepRules
{
    public static IReadOnlyList<KeepRule> All { get; } = Enum.GetValues<KeepRule>();

    public static string DisplayName(this KeepRule rule) => rule switch
    {
        KeepRule.Oldest => "Manter a mais antiga",
        KeepRule.Newest => "Manter a mais recente",
        _ => "Manter a que está fora de Downloads",
    };

    /// <summary>Ties on the date go to the shortest path, then ordinal path order.</summary>
    private static int ByPath(WalkedFile a, WalkedFile b) =>
        a.Path.Length != b.Path.Length ? a.Path.Length.CompareTo(b.Path.Length) : string.CompareOrdinal(a.Path, b.Path);

    /// <summary>Earliest creation first; an unknown creation date counts as the far future.</summary>
    private static int OldestFirst(WalkedFile a, WalkedFile b)
    {
        var x = a.CreatedUtc ?? DateTime.MaxValue;
        var y = b.CreatedUtc ?? DateTime.MaxValue;
        return x != y ? x.CompareTo(y) : ByPath(a, b);
    }

    /// <summary>Latest creation first; an unknown creation date counts as the far past. The modification date is
    /// ignored: Explorer copies keep it.</summary>
    private static int NewestFirst(WalkedFile a, WalkedFile b)
    {
        var x = a.CreatedUtc ?? DateTime.MinValue;
        var y = b.CreatedUtc ?? DateTime.MinValue;
        return x != y ? y.CompareTo(x) : ByPath(a, b);
    }

    private static WalkedFile? Min(IEnumerable<WalkedFile> files, Comparison<WalkedFile> order)
    {
        WalkedFile? best = null;
        foreach (var file in files)
        {
            if (best is null || order(file, best) < 0) best = file;
        }
        return best;
    }

    /// <summary>The copy to keep, or null for an empty group.</summary>
    public static WalkedFile? Keeper(this KeepRule rule, DuplicateGroup group, string home)
    {
        switch (rule)
        {
            case KeepRule.Oldest: return Min(group.Files, OldestFirst);
            case KeepRule.Newest: return Min(group.Files, NewestFirst);
            default:
                var downloads = Path.Combine(home, "Downloads");
                var outside = group.Files.Where(f => !SafetyPolicy.IsWithin(f.Path, downloads)).ToList();
                return Min(outside.Count > 0 ? outside : group.Files, OldestFirst);
        }
    }

    /// <summary>Paths to remove: every copy except the keeper of each group.</summary>
    public static HashSet<string> AutoSelect(IEnumerable<DuplicateGroup> groups, KeepRule rule, string home)
    {
        var result = new HashSet<string>();
        foreach (var group in groups)
        {
            var keep = rule.Keeper(group, home)?.Path;
            result.UnionWith(group.Files.Select(f => f.Path).Where(p => p != keep));
        }
        return result;
    }

    /// <summary>Guarantees at least one copy per group stays: if a group is fully selected, its keeper is unselected.
    /// Everything else in the selection is left as is.</summary>
    public static HashSet<string> EnforceKeepOne(IReadOnlySet<string> selection, IEnumerable<DuplicateGroup> groups, KeepRule rule, string home)
    {
        var safe = new HashSet<string>(selection);
        foreach (var group in groups)
        {
            if (group.Files.Count == 0 || !group.Files.All(f => selection.Contains(f.Path))) continue;
            if (rule.Keeper(group, home) is { } keep) safe.Remove(keep.Path);
        }
        return safe;
    }
}
