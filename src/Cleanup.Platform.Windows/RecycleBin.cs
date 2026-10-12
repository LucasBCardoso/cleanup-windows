using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Platform.Windows;

/// <summary>
/// The Windows Recycle Bin. Items are recycled through the shell (<c>SHFileOperation</c> with undo), then found again
/// through their <c>$I</c> metadata file in <c>&lt;drive&gt;\$Recycle.Bin\&lt;SID&gt;</c>, which names the <c>$R</c> item
/// holding the data. That location is what the history records so a cleanup can be undone.
/// </summary>
public static partial class RecycleBin
{
    public static string CurrentUserSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? "";

    /// <summary>A $I file: the original path and when it was deleted.</summary>
    public sealed record InfoFile(string InfoPath, string ItemPath, string OriginalPath, long Size, DateTime DeletedUtc);

    public static string FolderFor(string drive) => Path.Combine(Path.GetPathRoot(drive)!, "$Recycle.Bin", CurrentUserSid);

    /// <summary>Parses a $I file (version 1: Vista–8.1, version 2: Windows 10+). Null when it isn't one.</summary>
    public static InfoFile? ReadInfo(string infoPath)
    {
        try
        {
            var data = File.ReadAllBytes(infoPath);
            if (data.Length < 24) return null;
            var version = BitConverter.ToInt64(data, 0);
            var size = BitConverter.ToInt64(data, 8);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(data, 16));
            string original;
            if (version == 2)
            {
                if (data.Length < 28) return null;
                var chars = BitConverter.ToInt32(data, 24);
                if (chars <= 0 || 28 + chars * 2 > data.Length) return null;
                original = Encoding.Unicode.GetString(data, 28, chars * 2).TrimEnd('\0');
            }
            else if (version == 1)
            {
                original = Encoding.Unicode.GetString(data, 24, Math.Min(520, data.Length - 24)).Split('\0')[0];
            }
            else return null;
            var name = Path.GetFileName(infoPath);
            var item = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
            return new InfoFile(infoPath, item, original, size, deleted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The $I files of this user on the drives of <paramref name="paths"/> right now (full paths).</summary>
    public static HashSet<string> InfoFileNames(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in paths.Select(Path.GetPathRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = FolderFor(drive);
            if (!Directory.Exists(folder)) continue;
            try { names.UnionWith(Directory.EnumerateFiles(folder, "$I*", new EnumerationOptions { IgnoreInaccessible = true })); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return names;
    }

    /// <summary>
    /// $I files of this user on the drives of <paramref name="paths"/> that weren't there before (<paramref name="before"/>):
    /// what this recycle created, never an older item that happens to share an original path.
    /// </summary>
    public static List<InfoFile> NewInfoFiles(IEnumerable<string> paths, IReadOnlySet<string> before) =>
        InfoFileNames(paths).Where(n => !before.Contains(n)).Select(ReadInfo).OfType<InfoFile>().ToList();

    /// <summary>
    /// True for <c>&lt;drive&gt;:\$Recycle.Bin\&lt;current user SID&gt;</c> exactly, with neither component a link, so a
    /// history entry can't make restore move something from elsewhere.
    /// </summary>
    public static bool IsOwnRecycleBinFolder(string folder)
    {
        if (!PathUtil.IsNormalizedAbsolute(folder)) return false;
        var match = OwnFolderPattern().Match(folder);
        if (!match.Success || !string.Equals(match.Groups[2].Value, CurrentUserSid, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var bin = Path.GetDirectoryName(folder)!;
            return !File.GetAttributes(bin).HasFlag(FileAttributes.ReparsePoint)
                   && !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [GeneratedRegex(@"^([A-Za-z]):\\\$Recycle\.Bin\\(S-[0-9-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnFolderPattern();

    /// <summary><c>volume serial:file index</c> of the item itself (a link is not followed), or null when missing.</summary>
    public static string? FileId(string path)
    {
        using var handle = Native.CreateFile(path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!Native.GetFileInformationByHandle(handle, out var info)) return null;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"{info.VolumeSerialNumber:X8}:{index:X16}";
    }

    /// <summary>Moves a recycled item back, failing if the destination exists (never overwrites).</summary>
    public static void MoveBack(string source, string destination)
    {
        if (!Native.MoveFileEx(source, destination, Native.MOVEFILE_WRITE_THROUGH))
        {
            var error = Marshal.GetLastWin32Error();
            throw error is 80 or 183
                ? new IOException("já existe um item com esse nome no lugar original")
                : new IOException(new Win32Exception(error).Message);
        }
    }

    /// <summary>Removes the $I metadata of a restored $R item, so the Recycle Bin stops listing it.</summary>
    public static void DropInfoFor(string itemPath)
    {
        var name = Path.GetFileName(itemPath);
        if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return;
        var info = Path.Combine(Path.GetDirectoryName(itemPath)!, "$I" + name[2..]);
        if (File.Exists(info)) File.Delete(info);
    }

    public static Restorer.Context RestoreContext(SafetyPolicy policy) =>
        new(policy, IsOwnRecycleBinFolder, FileId, MoveBack, DropInfoFor);

    /// <summary>Total size and item count of the Recycle Bin on every drive.</summary>
    public static (long Size, long Items) Query()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static void Empty()
    {
        var hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
        // E_UNEXPECTED is what an already-empty Recycle Bin returns.
        if (hr != 0 && hr != unchecked((int)0x8000FFFF)) Marshal.ThrowExceptionForHR(hr);
    }
}

/// <summary>The real filesystem: Recycle Bin through the shell, permanent deletes that never follow links.</summary>
public sealed class WindowsFileRemover : IFileRemover
{
    private const int BatchSize = 64;

    /// <summary>
    /// Unattended (automatic cleaning): the shell must never show a "delete permanently?" dialog, so items the Recycle
    /// Bin can't take (bigger than its quota, or a drive set to delete immediately) are left in place and reported.
    /// </summary>
    public bool Background { get; init; }

    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths)
    {
        var outcomes = new List<RecycleOutcome>();
        if (Background)
        {
            var refused = paths.Select(p => (Path: p, Reason: RecycleBinLimits.WhyNot(p))).Where(r => r.Reason is not null).ToList();
            outcomes.AddRange(refused.Select(r => new RecycleOutcome(r.Path, false, null, r.Reason)));
            var skip = refused.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            paths = paths.Where(p => !skip.Contains(p)).ToList();
        }
        foreach (var batch in paths.Chunk(BatchSize))
        {
            var before = RecycleBin.InfoFileNames(batch);
            var error = ShellRecycle(batch);
            var left = batch.Where(Exists).ToList();
            // A failed batch may stop at its first locked item; give the others their own chance.
            var retried = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (left.Count > 0 && batch.Length > 1)
            {
                foreach (var path in left) retried[path] = ShellRecycle([path]);
            }
            // Only $I files this batch created; two new ones for the same path (something else recycled it at the same
            // moment) can't be told apart, so neither is recorded: not restorable beats restoring the wrong item.
            var located = RecycleBin.NewInfoFiles(batch, before)
                .GroupBy(i => i.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().ItemPath, StringComparer.OrdinalIgnoreCase);
            foreach (var path in batch)
            {
                if (Exists(path))
                {
                    var code = retried.TryGetValue(path, out var c) ? c : error;
                    outcomes.Add(new RecycleOutcome(path, false, null, Describe(code, path)));
                }
                else outcomes.Add(new RecycleOutcome(path, true, located.GetValueOrDefault(path), null));
            }
        }
        return outcomes;
    }

    private static int ShellRecycle(IReadOnlyCollection<string> paths)
    {
        if (paths.Any(p => p.Length >= 260)) return 206; // ERROR_FILENAME_EXCED_RANGE: the shell API can't take it
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOERRORUI | Native.FOF_SILENT
                              | Native.FOF_WANTNUKEWARNING),
        };
        var result = Native.SHFileOperation(ref op);
        return op.fAnyOperationsAborted && result == 0 ? 1223 : result;
    }

    private static string Describe(int code, string path) => code switch
    {
        0 => "não saiu do lugar",
        32 or 33 => "em uso por outro programa",
        5 or 0x78 => "acesso negado",
        206 => "caminho longo demais para a Lixeira",
        1223 => "cancelado",
        _ => $"não foi possível mover para a Lixeira (código {code})",
    };

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public void Delete(string path)
    {
        // By handle: links are removed as links, never entered, even if swapped in mid-delete.
        HandleDelete.Delete(path);
    }

    public void EmptyRecycleBin(long expectedBytes, long expectedItems)
    {
        // Only what the scan measured: anything deleted after it (in Explorer, by another app) is not destroyed.
        var (size, items) = RecycleBin.Query();
        if (size != expectedBytes || items != expectedItems)
            throw new IOException("a Lixeira mudou desde a verificação; verifique de novo antes de esvaziá-la");
        RecycleBin.Empty();
    }
}

/// <summary>The Recycle Bin as one Smart Scan item, measured by the shell.</summary>
public sealed class RecycleBinTask : IScanTask
{
    public string Id => "recycle-bin";
    public string Title => "Lixeira";
    public ScanCategory Category => ScanCategory.Trash;

    public ScanReport Scan(ScanEnvironment environment, SafetyPolicy policy, CancellationToken cancellation)
    {
        var (size, count) = RecycleBin.Query();
        List<ScanItem> items = size > 0 || count > 0
            ? [new ScanItem
            {
                Path = "shell:RecycleBinFolder", DisplayName = $"Lixeira ({count} {(count == 1 ? "item" : "itens")})", Size = size,
                RuleId = Id, Category = Category, Risk = RiskLevel.Safe, Kind = ItemKind.RecycleBin, ItemCount = count,
            }]
            : [];
        return new ScanReport(Id, Title, Category, items, []);
    }
}

public static class RunningProcesses
{
    /// <summary>Lowercase image names of running processes ("chrome", "slack"…).</summary>
    public static IReadOnlySet<string> Names()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { names.Add(p.ProcessName.ToLowerInvariant()); }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return names;
    }
}
