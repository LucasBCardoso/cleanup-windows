using Cleanup.Core.Scanning;

namespace Cleanup.Core.Duplicates;

public sealed record WalkOptions(long MinSize, int EntryLimit = 500_000, IReadOnlySet<string>? Extensions = null);

/// <summary>A regular file found by <see cref="FileWalker"/>.</summary>
public sealed record WalkedFile(string Path, long Size, DateTime? CreatedUtc, DateTime? ModifiedUtc, string FileId)
{
    /// <summary>Hard links share <see cref="FileId"/> (<c>volume serial:file index</c>).</summary>
    public string Id => Path;

    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record WalkResult(IReadOnlyList<WalkedFile> Files, IReadOnlyList<string> PartialRoots, IReadOnlyList<string> UnreadableRoots);

/// <summary>
/// A file's identity: <c>volume serial:file index</c> from <c>GetFileInformationByHandle</c> on Windows (hard links
/// share it). Injected because Core has no Windows APIs; null when the file can't be opened.
/// </summary>
public delegate string? FileIdentity(string path);

/// <summary>
/// Lists regular files under user-chosen folders. Never follows links (symlinks, junctions), skips hidden and system
/// entries and names starting with a dot, and never opens cloud-only placeholders (OneDrive "online only"), so a
/// search never downloads anything.
/// </summary>
public static class FileWalker
{
    /// <summary>When the platform gives no identity, the path stands in for it (no hard-link merging).</summary>
    public static string Identity(FileIdentity fileId, string path) => fileId(path) ?? "path:" + path.ToLowerInvariant();

    public static WalkResult Files(IEnumerable<string> roots, WalkOptions options, FileIdentity fileId, CancellationToken cancellation)
    {
        var files = new List<WalkedFile>();
        var partial = new List<string>();
        var unreadable = new List<string>();
        foreach (var given in FolderWalk.Outermost(roots))
        {
            var root = Resolve(given);
            if (root is null || FolderWalk.List(root) is null)
            {
                unreadable.Add(given);
                continue;
            }
            var budget = options.EntryLimit;
            if (!Walk(root, options, fileId, ref budget, files, cancellation)) partial.Add(given);
        }
        return new WalkResult(files, partial, unreadable);
    }

    /// <summary>A root picked by the user may itself be a link (a junction); then its target is walked.</summary>
    private static string? Resolve(string root)
    {
        try
        {
            var info = new DirectoryInfo(root);
            if (!info.Exists) return null;
            if (!FolderWalk.IsLink(info)) return root;
            return info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } target ? target.FullName : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>False when the entry budget ran out or the walk was cancelled.</summary>
    private static bool Walk(string dir, WalkOptions options, FileIdentity fileId, ref int budget, List<WalkedFile> into,
        CancellationToken cancellation)
    {
        var entries = FolderWalk.List(dir) ?? [];
        budget -= entries.Count;
        if (budget < 0 || cancellation.IsCancellationRequested) return false;
        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith('.')) continue;
            var attributes = entry.Attributes;
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            if (FolderWalk.IsLink(entry)) continue;
            if (entry is DirectoryInfo)
            {
                if (!Walk(entry.FullName, options, fileId, ref budget, into, cancellation)) return false;
                continue;
            }
            if (entry is not FileInfo file || FileMetrics.IsCloudOnly(attributes)) continue;
            if (file.Length < options.MinSize) continue;
            if (options.Extensions is { } extensions && !extensions.Contains(file.Extension.TrimStart('.').ToLowerInvariant())) continue;
            into.Add(new WalkedFile(file.FullName, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, Identity(fileId, file.FullName)));
        }
        return true;
    }
}
