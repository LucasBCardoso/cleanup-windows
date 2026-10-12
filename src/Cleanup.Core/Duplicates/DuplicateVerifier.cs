using Cleanup.Core.Scanning;

namespace Cleanup.Core.Duplicates;

/// <summary>Re-checks files right before removal: results can sit open while files are moved, edited or replaced.</summary>
public static class DuplicateVerifier
{
    /// <summary>
    /// The part of <paramref name="selection"/> that is still safe to remove:
    /// <list type="bullet">
    /// <item>a selected copy must still be the same file (same identity, same size) with the same head + tail bytes as a
    /// kept copy;</item>
    /// <item>each group must still have at least one unselected copy that is unchanged, or nothing in it is removed.</item>
    /// </list>
    /// Paths that are in no group pass through.
    /// </summary>
    public static HashSet<string> Verified(IReadOnlySet<string> selection, IEnumerable<DuplicateGroup> groups, FileIdentity fileId)
    {
        var groupList = groups.ToList();
        var safe = new HashSet<string>(selection);
        safe.ExceptWith(groupList.SelectMany(g => g.Files.Select(f => f.Path)));
        foreach (var group in groupList)
        {
            var keeper = group.Files.FirstOrDefault(f => !selection.Contains(f.Path) && Unchanged(f, group.Size, fileId));
            if (keeper is null || TryPartialHash(keeper.Path, group.Size) is not { } reference) continue;
            foreach (var file in group.Files)
            {
                if (!selection.Contains(file.Path) || !Unchanged(file, group.Size, fileId)) continue;
                if (TryPartialHash(file.Path, group.Size) == reference) safe.Add(file.Path);
            }
        }
        return safe;
    }

    /// <summary>Same regular file (same identity) and same size as when it was scanned; links never count.</summary>
    public static bool Unchanged(WalkedFile file, long size, FileIdentity fileId)
    {
        try
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || FolderWalk.IsLink(info) || FileMetrics.IsCloudOnly(info.Attributes) || info.Length != size) return false;
            return FileWalker.Identity(fileId, file.Path) == file.FileId;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? TryPartialHash(string path, long size)
    {
        try { return DuplicateFinder.PartialHash(path, size); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
