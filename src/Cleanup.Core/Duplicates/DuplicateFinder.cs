using System.Security.Cryptography;

namespace Cleanup.Core.Duplicates;

public sealed record DuplicateGroup(string Id, IReadOnlyList<WalkedFile> Files, long Size)
{
    /// <summary>Space freed by keeping one copy.</summary>
    public long Wasted => Size * Math.Max(0, Files.Count - 1);
}

/// <summary>
/// Byte-for-byte duplicates in stages: same size, then same first + last 64 KB, then same full SHA-256 (streamed).
/// Hard links (same volume and file index) count once: removing one frees nothing.
/// </summary>
public static class DuplicateFinder
{
    internal const int EdgeBytes = 64 * 1024;
    internal const int ChunkBytes = 1024 * 1024;

    /// <summary>Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static IReadOnlyList<DuplicateGroup> Groups(IEnumerable<WalkedFile> files, CancellationToken cancellation)
    {
        var seenIds = new HashSet<string>();
        var unique = files.Where(f => seenIds.Add(f.FileId)).ToList();
        var result = new List<DuplicateGroup>();
        foreach (var sameSize in unique.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var byEdges = new Dictionary<string, List<WalkedFile>>();
            foreach (var file in sameSize)
            {
                cancellation.ThrowIfCancellationRequested();
                if (TryHash(() => PartialHash(file.Path, file.Size)) is not { } hash) continue;
                (byEdges.TryGetValue(hash, out var list) ? list : byEdges[hash] = []).Add(file);
            }
            foreach (var candidates in byEdges.Values.Where(c => c.Count > 1))
            {
                var byContent = new Dictionary<string, List<WalkedFile>>();
                foreach (var file in candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (TryHash(() => FullHash(file.Path, cancellation)) is not { } hash) continue;
                    (byContent.TryGetValue(hash, out var list) ? list : byContent[hash] = []).Add(file);
                }
                foreach (var (hash, copies) in byContent.Where(p => p.Value.Count > 1))
                {
                    result.Add(new DuplicateGroup(hash, copies.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), copies[0].Size));
                }
            }
        }
        return result.OrderByDescending(g => g.Wasted).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    private static string? TryHash(Func<string> hash)
    {
        try { return hash(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static FileStream OpenRead(string path, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, options);

    /// <summary>SHA-256 of the first and last 64 KB (the whole file when it is smaller than both together).</summary>
    public static string PartialHash(string path, long size)
    {
        using var stream = OpenRead(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[EdgeBytes];
        if (size <= EdgeBytes * 2L)
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hasher.AppendData(buffer, 0, read);
        }
        else
        {
            hasher.AppendData(buffer, 0, stream.ReadAtLeast(buffer, EdgeBytes, throwOnEndOfStream: false));
            stream.Seek(size - EdgeBytes, SeekOrigin.Begin);
            hasher.AppendData(buffer, 0, stream.ReadAtLeast(buffer, EdgeBytes, throwOnEndOfStream: false));
        }
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>Streamed SHA-256 of the whole file (constant memory).</summary>
    public static string FullHash(string path, CancellationToken cancellation = default)
    {
        using var stream = OpenRead(path, FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
