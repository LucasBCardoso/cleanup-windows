using System.IO.Enumeration;

namespace Cleanup.Core.Scanning;

/// <summary>A folder's measured size, and how many subfolders couldn't be read (so the size is a lower bound).</summary>
public readonly record struct DirectoryMeasure(long Bytes, int Unreadable)
{
    public bool IsPartial => Unreadable > 0;
}

/// <summary>
/// Size and date helpers. Links (junctions, symlinks, mount points) are never followed: a link into a huge folder
/// counts as nothing. Other reparse points are ordinary data — every file and folder OneDrive syncs carries one — so
/// they are walked. Cloud-only files (OneDrive placeholders) take no space and are never opened, so scanning never
/// downloads them.
/// </summary>
public static class FileMetrics
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: content lives in the cloud.</summary>
    internal const FileAttributes CloudOnly = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnly) != 0;

    /// <summary>
    /// True for a link: a junction, a symbolic link or a mount point (anything with a link target). False for a plain
    /// entry or a reparse point that holds the data itself (OneDrive, deduplicated or compressed files).
    /// </summary>
    public static bool IsLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // can't tell: treat as a link, never follow it
        }
    }

    /// <summary>Bytes freed by removing <paramref name="path"/>: a file's length, or the files under a folder.</summary>
    public static long Size(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }

        if (IsLink(path, attributes)) return 0;
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            if (IsCloudOnly(attributes)) return 0;
            try { return new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
        return DirectorySize(path, CancellationToken.None);
    }

    public static long DirectorySize(string path, CancellationToken cancellation) => Measure(path, cancellation).Bytes;

    /// <summary>The files under a folder (links not followed, cloud-only files not counted), and what couldn't be read.</summary>
    public static DirectoryMeasure Measure(string path, CancellationToken cancellation)
    {
        try
        {
            using var walk = new SizeWalk(path);
            long total = 0;
            var count = 0;
            while (walk.MoveNext())
            {
                total += walk.Current;
                if (++count % 4096 == 0) cancellation.ThrowIfCancellationRequested();
            }
            return new DirectoryMeasure(total, walk.Unreadable);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new DirectoryMeasure(0, 1); }
    }

    /// <summary>Recursive walk that steps into every real folder (OneDrive's included) but never into a link, and counts errors.</summary>
    private sealed class SizeWalk(string root) : FileSystemEnumerator<long>(root, new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    })
    {
        public int Unreadable { get; private set; }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => !entry.IsDirectory && !IsCloudOnly(entry.Attributes);

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) == 0 || !IsLink(entry.ToFullPath(), entry.Attributes);

        protected override long TransformEntry(ref FileSystemEntry entry) => entry.Length;

        protected override bool ContinueOnError(int error)
        {
            Unreadable++;
            return true;
        }
    }

    public static DateTime? LastWriteUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return info.LastWriteTimeUtc;
            var dir = new DirectoryInfo(path);
            return dir.Exists ? dir.LastWriteTimeUtc : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>NTFS often doesn't update last-access times, so callers fall back to the last write (design §5.2).</summary>
    public static DateTime? LastAccessUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return info.LastAccessTimeUtc;
            var dir = new DirectoryInfo(path);
            return dir.Exists ? dir.LastAccessTimeUtc : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
