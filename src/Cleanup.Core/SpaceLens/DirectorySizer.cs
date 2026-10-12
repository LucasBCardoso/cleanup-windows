using Cleanup.Core.Scanning;

namespace Cleanup.Core.SpaceLens;

/// <param name="IsPartial">Some subfolders couldn't be read (permissions): the size is a lower bound.</param>
public sealed record DirectoryEntry(string Path, long Size, bool IsDirectory, bool IsLink, bool IsPartial = false)
{
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path;
}

/// <summary>
/// One level of a Space Lens breakdown: immediate children of a folder sized recursively, measured concurrently,
/// largest first. Junctions and symlinks are listed but neither followed nor counted; OneDrive folders (reparse points
/// that hold data) are measured. Hidden and system items count too (hiberfil.sys, pagefile.sys, the Recycle Bin, restore
/// points): they use the disk like anything else.
/// </summary>
public static class DirectorySizer
{
    public static async Task<IReadOnlyList<DirectoryEntry>> ChildrenAsync(string directory, bool includeHidden = false,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = 0 };
        var children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToList();
        var entries = new DirectoryEntry?[children.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, children.Count),
            new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
            (index, token) =>
            {
                var info = children[index];
                var attributes = info.Attributes;
                var isLink = FileMetrics.IsLink(info.FullName, attributes);
                var isDir = attributes.HasFlag(FileAttributes.Directory);
                // Hidden + system links are Windows' compatibility junctions ("Documents and Settings", "Application
                // Data"…): pointers to folders listed elsewhere, so they'd only add noise.
                var legacyJunction = isLink && attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System);
                if (!legacyJunction || includeHidden)
                {
                    DirectoryEntry entry;
                    if (isLink) entry = new DirectoryEntry(info.FullName, 0, false, true);
                    else if (isDir)
                    {
                        var measure = FileMetrics.Measure(info.FullName, token);
                        entry = new DirectoryEntry(info.FullName, measure.Bytes, true, false, measure.IsPartial);
                    }
                    else entry = new DirectoryEntry(info.FullName, FileMetrics.IsCloudOnly(attributes) ? 0 : ((FileInfo)info).Length, false, false);
                    entries[index] = entry;
                }
                progress?.Report(Interlocked.Increment(ref done));
                return ValueTask.CompletedTask;
            });
        return entries.OfType<DirectoryEntry>().OrderByDescending(e => e.Size).ToList();
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
