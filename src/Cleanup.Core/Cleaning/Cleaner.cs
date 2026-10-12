using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Cleaning;

public enum RemovalMode
{
    /// <summary>Report what would happen; touch nothing.</summary>
    DryRun,
    /// <summary>Move to the Recycle Bin (recoverable). Default.</summary>
    RecycleBin,
    /// <summary>Delete immediately (not recoverable).</summary>
    Permanent,
}

/// <summary>Outcome of sending one path to the Recycle Bin.</summary>
public sealed record RecycleOutcome(string Path, bool Removed, string? RecycleBinPath, string? Error);

/// <summary>The filesystem, behind an interface so tests can observe removals (the Swift <c>FileRemoving</c>).</summary>
public interface IFileRemover
{
    /// <summary>Moves paths to the Recycle Bin; returns, per path, whether it left and where it landed when known.</summary>
    IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths);

    /// <summary>Deletes permanently. A link (junction, symlink) is removed as a link, never followed.</summary>
    void Delete(string path);

    /// <summary>
    /// Empties the Recycle Bin of every drive, only if it still holds what the scan measured (bytes and item count);
    /// otherwise throws, so nothing put there after the scan is destroyed.
    /// </summary>
    void EmptyRecycleBin(long expectedBytes, long expectedItems);
}

public sealed record CleanFailure(ScanItem Item, string Reason);

public sealed record CleanResult(RemovalMode Mode, IReadOnlyList<ScanItem> Removed, IReadOnlyList<CleanFailure> Failed,
    IReadOnlyDictionary<string, string> RecycleBinLocations)
{
    public long FreedBytes => Removed.Sum(i => i.Size);
}

/// <summary>
/// Removes scan items after re-checking each one against the safety policy (invariant 2). Items needing admin are
/// never removed here: they go to the elevated helper.
/// </summary>
public sealed class Cleaner(SafetyPolicy policy, IFileRemover remover, RemovalMode mode = RemovalMode.RecycleBin)
{
    public RemovalMode Mode => mode;

    public CleanResult Clean(IReadOnlyList<ScanItem> items)
    {
        var removed = new List<ScanItem>();
        var failed = new List<CleanFailure>();
        var allowed = new List<ScanItem>();
        var emptyBin = false;
        // Check safety first, so a blocked parent never hides an allowed child during dedup.
        foreach (var item in items)
        {
            if (item.Kind == ItemKind.RecycleBin) { emptyBin = true; continue; }
            if (item.RequiresAdmin) { failed.Add(new(item, "requer administrador")); continue; }
            if (policy.Verdict(item.Path) is SafetyVerdict.Denied denied)
                failed.Add(new(item, "bloqueado pela política de segurança: " + denied.Reason));
            else
                allowed.Add(item);
        }

        // The Recycle Bin is emptied BEFORE anything is moved there, so a clean never destroys what it recycles.
        if (emptyBin)
        {
            var bin = items.First(i => i.Kind == ItemKind.RecycleBin);
            if (mode == RemovalMode.DryRun) removed.Add(bin);
            else
            {
                try { remover.EmptyRecycleBin(bin.Size, bin.ItemCount ?? 0); removed.Add(bin); }
                catch (Exception e) { failed.Add(new(bin, Describe(e))); }
            }
        }

        var locations = new Dictionary<string, string>();
        var unique = Deduplicated(allowed);
        switch (mode)
        {
            case RemovalMode.DryRun:
                removed.AddRange(unique);
                break;
            case RemovalMode.Permanent:
                foreach (var item in unique)
                {
                    try { remover.Delete(item.Path); removed.Add(item); }
                    catch (Exception e) { failed.Add(new(item, Describe(e))); }
                }
                break;
            case RemovalMode.RecycleBin:
                var byPath = unique.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var outcome in remover.Recycle(unique.Select(i => i.Path).ToList()))
                {
                    if (!byPath.TryGetValue(outcome.Path, out var item)) continue;
                    if (outcome.Removed)
                    {
                        removed.Add(item);
                        if (outcome.RecycleBinPath is { } location) locations[item.Id] = location;
                    }
                    else failed.Add(new(item, outcome.Error ?? "não foi possível mover para a Lixeira"));
                }
                break;
        }

        return new CleanResult(mode, removed, failed, locations);
    }

    internal static string Describe(Exception e) => e switch
    {
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "em uso por outro programa",
        UnauthorizedAccessException => "acesso negado",
        _ => e.Message,
    };

    /// <summary>Drops items nested inside another selected item, so a parent and its child are never both removed.</summary>
    internal static List<ScanItem> Deduplicated(IEnumerable<ScanItem> items)
    {
        var kept = new List<ScanItem>();
        foreach (var item in items.OrderBy(i => i.Path.Length))
        {
            if (!kept.Any(k => SafetyPolicy.IsWithin(item.Path, k.Path))) kept.Add(item);
        }
        return kept;
    }
}
