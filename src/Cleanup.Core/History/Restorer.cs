using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.History;

/// <summary>
/// Puts the items of a cleanup back where they were (invariant 7). The history file is user-writable, so nothing in
/// it is trusted: sources must really be <c>$R…</c> items in this user's Recycle Bin folder, destinations must be
/// absolute, normalized and allowed by <see cref="SafetyPolicy"/>, the item must still be the one that was recycled
/// (file identity), and the move never replaces anything.
/// </summary>
public static class Restorer
{
    /// <summary>Platform hooks; tests replace them.</summary>
    public sealed record Context(
        SafetyPolicy Policy,
        /// <summary>True when the folder is <c>&lt;drive&gt;\$Recycle.Bin\&lt;current user SID&gt;</c>, reached without links.</summary>
        Func<string, bool> IsOwnRecycleBinFolder,
        Func<string, string?> FileId,
        /// <summary>Moves an item back; must fail if the destination exists.</summary>
        Action<string, string> Mover,
        /// <summary>Called after a successful move with the <c>$R</c> path, to drop the matching <c>$I</c> file.</summary>
        Action<string>? AfterRestore = null);

    public sealed class RestoreException(string message) : Exception(message);

    public sealed record Availability(int RestorableCount, long RestorableBytes, int GoneCount, int BlockedCount, int PermanentCount);

    public sealed record Failure(CleanupRecord.Entry Entry, string Reason);

    public sealed record Result(IReadOnlyList<CleanupRecord.Entry> Restored, IReadOnlyList<Failure> Failed, CleanupRecord? Remaining)
    {
        public long RestoredBytes => Restored.Sum(e => e.Size);
    }

    internal abstract record State
    {
        public sealed record Restorable : State;
        public sealed record Gone : State;
        public sealed record Blocked(string Reason) : State;
        public sealed record Permanent : State;
    }

    public static Availability AvailabilityOf(CleanupRecord record, Context context)
    {
        int restorable = 0, gone = 0, blocked = 0, permanent = 0;
        long bytes = 0;
        foreach (var entry in record.Entries)
        {
            switch (StateOf(entry, context))
            {
                case State.Restorable: restorable++; bytes += entry.Size; break;
                case State.Gone: gone++; break;
                case State.Blocked: blocked++; break;
                default: permanent++; break;
            }
        }
        return new Availability(restorable, bytes, gone, blocked, permanent);
    }

    public static bool IsRestorable(CleanupRecord.Entry entry, Context context) => StateOf(entry, context) is State.Restorable;

    public static Result Restore(CleanupRecord record, Context context)
    {
        var restored = new List<CleanupRecord.Entry>();
        var failed = new List<Failure>();
        foreach (var entry in record.Entries)
        {
            switch (StateOf(entry, context))
            {
                case State.Permanent: continue;
                case State.Gone: failed.Add(new(entry, "não está mais na Lixeira")); break;
                case State.Blocked b: failed.Add(new(entry, b.Reason)); break;
                case State.Restorable:
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
                        context.Mover(entry.TrashPath!, entry.OriginalPath);
                        restored.Add(entry);
                        try { context.AfterRestore?.Invoke(entry.TrashPath!); } catch (IOException) { }
                    }
                    catch (Exception e)
                    {
                        failed.Add(new(entry, e is IOException && (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
                            ? "já existe um item com esse nome no lugar original" : e.Message));
                    }
                    break;
            }
        }
        var left = record.Entries.Where(e => !restored.Contains(e)).ToList();
        return new Result(restored, failed, left.Count == 0 ? null : record with { Entries = left });
    }

    internal static State StateOf(CleanupRecord.Entry entry, Context context)
    {
        if (entry.TrashPath is not { } trashPath) return new State.Permanent();
        if (!PathUtil.IsNormalizedAbsolute(entry.OriginalPath) || !context.Policy.Verdict(entry.OriginalPath).IsAllowed)
            return new State.Blocked("o lugar original não é permitido");
        if (!PathUtil.IsNormalizedAbsolute(trashPath) || !Path.GetFileName(trashPath).StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            return new State.Blocked("não é um item da Lixeira");
        var parent = Path.GetDirectoryName(trashPath);
        if (parent is null || !Directory.Exists(parent)) return new State.Gone();
        if (!context.IsOwnRecycleBinFolder(parent)) return new State.Blocked("não é um item da Lixeira");
        if (context.FileId(trashPath) is not { } currentId) return new State.Gone();
        // No recorded identity (history file edited, or it couldn't be read at the time): never trust a name alone.
        if (entry.FileId is not { } expected) return new State.Blocked("sem identidade do item na Lixeira");
        if (expected != currentId) return new State.Blocked("o item na Lixeira mudou");
        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
            return new State.Blocked("já existe um item com esse nome no lugar original");
        return new State.Restorable();
    }
}
