using System.Collections.Concurrent;
using System.IO.Enumeration;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.SpaceLens;

/// <summary>
/// One walk measures every folder under a root and keeps the result, like WinDirStat or WizTree: opening a subfolder
/// afterwards is instant instead of walking it again. Same rules as <see cref="FileMetrics"/>: links are listed but never
/// followed, OneDrive folders are walked, cloud-only files count as nothing, unreadable folders make the sizes above them
/// a lower bound.
/// </summary>
public sealed class SpaceTree
{
    private readonly ConcurrentDictionary<string, DirectoryMeasure> folders = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }
    public DirectoryMeasure Total { get; private set; }

    private SpaceTree(string root) => Root = root;

    public bool Covers(string path) => SafetyPolicy.IsWithin(path, Root);

    /// <param name="reuse">A tree measured earlier for a folder inside <paramref name="root"/> (going up): its folders aren't walked again.</param>
    /// <param name="filesSeen">Running count of files measured, for progress.</param>
    public static Task<SpaceTree> BuildAsync(string root, SpaceTree? reuse = null, IProgress<long>? filesSeen = null,
        CancellationToken cancellation = default) => Task.Run(() =>
    {
        var tree = new SpaceTree(root);
        if (reuse is not null && tree.Covers(reuse.Root))
        {
            foreach (var (path, measure) in reuse.folders) tree.folders[path] = measure;
        }
        var counter = new Counter(filesSeen);
        tree.Total = tree.Walk(root, depth: 0, counter, cancellation);
        counter.Flush();
        return tree;
    }, cancellation);

    private sealed class Counter(IProgress<long>? progress)
    {
        private long files;
        private long lastReport;

        public void Add(long n)
        {
            var now = Interlocked.Add(ref files, n);
            if (progress is not null && now - Interlocked.Read(ref lastReport) >= 5000)
            {
                Interlocked.Exchange(ref lastReport, now);
                progress.Report(now);
            }
        }

        public void Flush() => progress?.Report(Interlocked.Read(ref files));
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes);

    private DirectoryMeasure Walk(string directory, int depth, Counter counter, CancellationToken cancellation)
    {
        if (folders.TryGetValue(directory, out var known)) return known;
        cancellation.ThrowIfCancellationRequested();
        List<Entry> entries;
        try
        {
            entries = new FileSystemEnumerable<Entry>(directory,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes),
                new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            var unreadable = new DirectoryMeasure(0, 1);
            folders[directory] = unreadable;
            return unreadable;
        }

        long bytes = 0;
        var unreadableBelow = 0;
        var files = 0;
        var subfolders = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                if (!FileMetrics.IsCloudOnly(entry.Attributes)) bytes += entry.Length;
                files++;
                continue;
            }
            var path = Path.Combine(directory, entry.Name);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && FileMetrics.IsLink(path, entry.Attributes)) continue;
            subfolders.Add(path);
        }
        counter.Add(files);

        // The first two levels in parallel: that's where the big independent subtrees are.
        if (depth < 2 && subfolders.Count > 1)
        {
            var results = new DirectoryMeasure[subfolders.Count];
            Parallel.For(0, subfolders.Count, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Environment.ProcessorCount },
                i => results[i] = Walk(subfolders[i], depth + 1, counter, cancellation));
            foreach (var r in results) { bytes += r.Bytes; unreadableBelow += r.Unreadable; }
        }
        else
        {
            foreach (var sub in subfolders)
            {
                var r = Walk(sub, depth + 1, counter, cancellation);
                bytes += r.Bytes;
                unreadableBelow += r.Unreadable;
            }
        }
        var measure = new DirectoryMeasure(bytes, unreadableBelow);
        folders[directory] = measure;
        return measure;
    }

    /// <summary>
    /// An item left (moved to the Recycle Bin): its bytes come off every folder above it, up to the root, so the
    /// listing stays right without walking again.
    /// </summary>
    public void Removed(string path, long bytes)
    {
        foreach (var key in folders.Keys.Where(k => SafetyPolicy.IsWithin(k, path)).ToList()) folders.TryRemove(key, out _);
        for (var parent = Path.GetDirectoryName(path); parent is not null && Covers(parent); parent = Path.GetDirectoryName(parent))
        {
            if (folders.TryGetValue(parent, out var m)) folders[parent] = m with { Bytes = Math.Max(0, m.Bytes - bytes) };
        }
        Total = Total with { Bytes = Math.Max(0, Total.Bytes - bytes) };
    }

    /// <summary>One level of <paramref name="directory"/> (inside <see cref="Root"/>), largest first, sizes from the walk.</summary>
    public IReadOnlyList<DirectoryEntry> Children(string directory, bool includeHidden = false)
    {
        var result = new List<DirectoryEntry>();
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*",
                new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = 0 }).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return result; }

        foreach (var info in children)
        {
            var attributes = info.Attributes;
            var isDir = attributes.HasFlag(FileAttributes.Directory);
            var isLink = (attributes & FileAttributes.ReparsePoint) != 0 && FileMetrics.IsLink(info.FullName, attributes);
            // Hidden + system links are Windows' compatibility junctions ("Documents and Settings"…): noise.
            if (isLink && attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System) && !includeHidden) continue;
            if (isLink) { result.Add(new DirectoryEntry(info.FullName, 0, false, true)); continue; }
            if (isDir)
            {
                var measure = folders.TryGetValue(info.FullName, out var m) ? m : FileMetrics.Measure(info.FullName, CancellationToken.None);
                result.Add(new DirectoryEntry(info.FullName, measure.Bytes, true, false, measure.IsPartial));
            }
            else
            {
                result.Add(new DirectoryEntry(info.FullName, FileMetrics.IsCloudOnly(attributes) ? 0 : ((FileInfo)info).Length, false, false));
            }
        }
        return result.OrderByDescending(e => e.Size).ToList();
    }
}
