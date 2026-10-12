using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.History;

/// <summary>One cleanup: what was removed, and where each item went in the Recycle Bin (so it can be put back).</summary>
public sealed record CleanupRecord
{
    public sealed record Entry
    {
        [JsonPropertyName("originalPath")] public required string OriginalPath { get; init; }
        /// <summary>The <c>$R…</c> item in the Recycle Bin; null when deleted outright (admin items, emptied bin).</summary>
        [JsonPropertyName("trashPath")] public string? TrashPath { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        /// <summary>
        /// Identity (<c>volume serial:file index</c>) of the item in the Recycle Bin, taken right after the move. A later
        /// item under the same name has a different one, so it is never put back in this entry's place.
        /// </summary>
        [JsonPropertyName("fileID")] public string? FileId { get; init; }

        internal Entry WithoutTrashLocation() => this with { TrashPath = null, FileId = null };
    }

    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; }
    /// <summary>Where the cleanup came from ("Smart Scan", "Desinstalador", "cleanup clean"…).</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("entries")] public required IReadOnlyList<Entry> Entries { get; init; }

    [JsonIgnore] public long TotalBytes => Entries.Sum(e => e.Size);

    /// <summary>A record of what a clean actually removed, or null when nothing was removed.</summary>
    public static CleanupRecord? From(CleanResult result, IEnumerable<ScanItem>? adminRemoved, string source,
        Func<string, string?> fileId, DateTimeOffset? date = null)
    {
        if (result.Mode == RemovalMode.DryRun) return null;
        var trashed = result.Removed.Select(item =>
        {
            var trashPath = result.RecycleBinLocations.GetValueOrDefault(item.Id);
            return new Entry
            {
                OriginalPath = item.Kind == ItemKind.RecycleBin ? "Lixeira" : item.Path, TrashPath = trashPath,
                Size = item.Size, FileId = trashPath is null ? null : fileId(trashPath),
            };
        });
        var deleted = (adminRemoved ?? []).Select(i => new Entry { OriginalPath = i.Path, Size = i.Size });
        var entries = trashed.Concat(deleted).ToList();
        return entries.Count == 0 ? null : new CleanupRecord { Date = date ?? DateTimeOffset.Now, Source = source, Entries = entries };
    }
}

public sealed class CleanupHistoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The newest cleanups, newest first. Value type, like the Swift one.</summary>
public sealed class CleanupHistory
{
    public const int Limit = 30;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<CleanupRecord> Records { get; }

    /// <summary>Newest first, capped at <see cref="Limit"/>. A repeated id (the file is user-writable) keeps its first record.</summary>
    public CleanupHistory(IEnumerable<CleanupRecord>? records = null)
    {
        var seen = new HashSet<Guid>();
        Records = (records ?? []).Where(r => seen.Add(r.Id)).OrderByDescending(r => r.Date).Take(Limit).ToList();
    }

    public static string DefaultPath => Path.Combine(AppPaths.SupportDirectory, "cleanup-history.json");

    /// <summary>
    /// Adds a record. Older entries whose Recycle Bin location the new record now uses no longer point at their item,
    /// so they stop being restorable.
    /// </summary>
    public CleanupHistory Adding(CleanupRecord record)
    {
        var reused = record.Entries.Select(e => e.TrashPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var older = Records.Select(old =>
        {
            if (!old.Entries.Any(e => e.TrashPath is { } t && reused.Contains(t))) return old;
            return old with { Entries = old.Entries.Select(e => e.TrashPath is { } t && reused.Contains(t) ? e.WithoutTrashLocation() : e).ToList() };
        });
        return new CleanupHistory(older.Append(record));
    }

    /// <summary>Drops restored entries from the record as it is now; a record with nothing left is removed.</summary>
    public CleanupHistory ApplyingRestore(Guid id, IReadOnlyList<CleanupRecord.Entry> restored)
    {
        if (Records.FirstOrDefault(r => r.Id == id) is not { } current) return this;
        var left = current.Entries.Where(e => !restored.Contains(e)).ToList();
        return left.Count == 0 ? Removing(id) : Replacing(current with { Entries = left });
    }

    public CleanupHistory Replacing(CleanupRecord record) => new(Records.Select(r => r.Id == record.Id ? record : r));

    public CleanupHistory Removing(Guid id) => new(Records.Where(r => r.Id != id));

    /// <summary>A missing file is an empty history; an unreadable one throws (never silently reset).</summary>
    public static CleanupHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new CleanupHistory();
        try
        {
            return new CleanupHistory(JsonSerializer.Deserialize<List<CleanupRecord>>(File.ReadAllText(path), Json) ?? []);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CleanupHistoryException($"O histórico em {path} não pôde ser lido: {e.Message}", e);
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Records, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CleanupHistoryException($"O histórico não pôde ser salvo em {path}: {e.Message}", e);
        }
    }

    public sealed record UpdateOutcome(CleanupHistory History, string? MovedAside);

    private static readonly object ProcessLock = new();

    /// <summary>
    /// Loads, applies <paramref name="change"/> and saves while holding a lock — in-process and, through an exclusive
    /// sidecar <c>.lock</c> file, across processes — so the app and the CLI don't drop each other's records. A corrupt
    /// file is moved aside (<c>cleanup-history.corrupt-&lt;date&gt;.json</c>) and a fresh history started.
    /// </summary>
    public static UpdateOutcome Update(Func<CleanupHistory, CleanupHistory> change, string? path = null)
    {
        path ??= DefaultPath;
        lock (ProcessLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fileLock = AcquireLock(path + ".lock");
            string? movedAside = null;
            CleanupHistory current;
            try { current = Load(path); }
            catch (CleanupHistoryException)
            {
                movedAside = Path.Combine(Path.GetDirectoryName(path)!, $"cleanup-history.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(path, movedAside);
                current = new CleanupHistory();
            }
            var updated = change(current);
            updated.Save(path);
            return new UpdateOutcome(updated, movedAside);
        }
    }

    private static FileStream AcquireLock(string lockPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200) { Thread.Sleep(50); }
        }
    }
}
