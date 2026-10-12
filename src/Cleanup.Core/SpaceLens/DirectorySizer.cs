using Cleanup.Core.Scanning;

namespace Cleanup.Core.SpaceLens;

public sealed record DirectoryEntry(string Path, long Size, bool IsDirectory, bool IsLink)
{
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path;
}

/// <summary>
/// One level of a Space Lens breakdown: immediate children of a folder sized recursively, measured concurrently,
/// largest first. Junctions and symlinks are listed but neither followed nor counted.
/// </summary>
public static class DirectorySizer
{
    public static async Task<IReadOnlyList<DirectoryEntry>> ChildrenAsync(string directory, bool includeHidden = false,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, ReturnSpecialDirectories = false,
            AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        var children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToList();
        var entries = new DirectoryEntry[children.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, children.Count),
            new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
            (index, token) =>
            {
                var info = children[index];
                var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                var isDir = info.Attributes.HasFlag(FileAttributes.Directory);
                long size = isLink ? 0
                    : isDir ? FileMetrics.DirectorySize(info.FullName, token)
                    : FileMetrics.IsCloudOnly(info.Attributes) ? 0 : ((FileInfo)info).Length;
                entries[index] = new DirectoryEntry(info.FullName, size, isDir && !isLink, isLink);
                progress?.Report(Interlocked.Increment(ref done));
                return ValueTask.CompletedTask;
            });
        return entries.OrderByDescending(e => e.Size).ToList();
    }
}

/// <summary>
/// Where a drive stands. On Windows "purgeable" is what the OS can reclaim by itself; DriveInfo reports none, so the
/// app passes <c>importantAvailable = available</c> until Storage Sense data is read (design §5.2, W8d).
/// </summary>
public sealed record DiskSpace(long Total, long Available, long ImportantAvailable)
{
    public long Purgeable => Math.Max(0, ImportantAvailable - Available);
    public long Used => Math.Max(0, Total - Math.Max(ImportantAvailable, Available));
    public long Free => Available;

    public static DiskSpace? Of(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            return info.IsReady ? new DiskSpace(info.TotalSize, info.AvailableFreeSpace, info.AvailableFreeSpace) : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
