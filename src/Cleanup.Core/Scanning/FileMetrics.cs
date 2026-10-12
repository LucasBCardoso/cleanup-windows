using System.IO.Enumeration;

namespace Cleanup.Core.Scanning;

/// <summary>
/// Size and date helpers. Links (junctions, symlinks) are never followed: a link into a huge folder counts as nothing.
/// Cloud-only files (OneDrive placeholders) take no space and are never opened, so scanning never downloads them.
/// </summary>
public static class FileMetrics
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: content lives in the cloud.</summary>
    internal const FileAttributes CloudOnly = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnly) != 0;

    /// <summary>Bytes freed by removing <paramref name="path"/>: a file's length, or the files under a folder.</summary>
    public static long Size(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            if (IsCloudOnly(attributes)) return 0;
            try { return new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
        return DirectorySize(path, CancellationToken.None);
    }

    public static long DirectorySize(string path, CancellationToken cancellation)
    {
        try
        {
            var files = new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length, Recursive)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !IsCloudOnly(e.Attributes),
            };
            long total = 0;
            var count = 0;
            foreach (var length in files)
            {
                total += length;
                if (++count % 4096 == 0) cancellation.ThrowIfCancellationRequested();
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
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
