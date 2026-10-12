using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Scanning;

/// <summary>
/// Helpers for walking folders the user picked (Duplicados, Desenvolvedor) without ever following a link
/// (invariants 3 and 13).
/// </summary>
public static class FolderWalk
{
    /// <summary>
    /// A reparse point that redirects somewhere else: symlink, junction or mount point. OneDrive files and folders
    /// are reparse points too (cloud tags) but hold their own content, so they are not links and are walked.
    /// When in doubt (the target can't be read) the entry counts as a link and is skipped.
    /// </summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>An existing folder that is not a link.</summary>
    public static bool IsRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && !IsLink(info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>An existing regular file that is not a link.</summary>
    public static bool IsRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && !IsLink(info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Drops repeated roots and roots inside another root, so nothing is walked (or counted) twice.</summary>
    public static IReadOnlyList<string> Outermost(IEnumerable<string> roots)
    {
        var paths = new List<string>();
        foreach (var root in roots)
        {
            try { paths.Add(PathUtil.Normalize(root)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var result = new List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (result.Any(r => PathUtil.SameOrdinalIgnoreCase(r, path))) continue;
            var insideAnother = paths.Where((other, j) => j != i && !PathUtil.SameOrdinalIgnoreCase(other, path)
                                                          && SafetyPolicy.IsWithin(path, other)).Any();
            if (!insideAnother) result.Add(path);
        }
        return result;
    }

    /// <summary>The entries of one folder, sorted by name; null when the folder can't be listed.</summary>
    public static List<FileSystemInfo>? List(string directory)
    {
        try
        {
            var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            }).ToList();
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}
