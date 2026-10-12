using Cleanup.Core.Rules;

namespace Cleanup.Core.Models;

/// <summary>What a scan item stands for: a path on disk, or the whole Recycle Bin (emptied through the shell).</summary>
public enum ItemKind
{
    Path,
    RecycleBin,
}

/// <summary>One removable entry found by a scan task. Mirrors the Swift <c>ScanItem</c>.</summary>
public sealed record ScanItem
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required string RuleId { get; init; }
    public required ScanCategory Category { get; init; }
    public required RiskLevel Risk { get; init; }
    public DateTime? LastAccessedUtc { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    /// <summary>System junk: removed permanently by the elevated helper, never moved to the Recycle Bin.</summary>
    public bool RequiresAdmin { get; init; }
    public ItemKind Kind { get; init; } = ItemKind.Path;
    /// <summary>Shown instead of the path when set (e.g. "Lixeira").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Stable id for selection: the path, compared case-insensitively.</summary>
    public string Id => Kind == ItemKind.RecycleBin ? "recycle-bin:" : Path.ToLowerInvariant();

    public string Name => DisplayName ?? (System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path);
}

/// <summary>Output of one scan task. Unreadable locations are reported, never swallowed.</summary>
public sealed record ScanReport(string TaskId, string Title, ScanCategory Category, IReadOnlyList<ScanItem> Items,
    IReadOnlyList<string> Skipped)
{
    public long TotalSize => Items.Sum(i => i.Size);
}

/// <summary>A report, or the error its task failed with. A failing task never hides the others.</summary>
public abstract record ScanOutcome
{
    public sealed record Success(ScanReport Value) : ScanOutcome;
    public sealed record Failure(string TaskId, string Title, string Message) : ScanOutcome;

    public ScanReport? Report => (this as Success)?.Value;
}

public readonly record struct ScanProgress(int Completed, int Total, string LastTaskTitle)
{
    public double Fraction => Total == 0 ? 1 : (double)Completed / Total;
}

public static class ByteFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];
    private static readonly System.Globalization.CultureInfo PtBr = new("pt-BR");

    /// <summary>Decimal units like Finder and the macOS app: 1 KB = 1000 bytes. "1,2 GB", "0 bytes".</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1) { value /= 1000; unit++; }
        var format = value >= 100 || unit == 1 ? "0" : "0.#";
        return value.ToString(format, PtBr) + " " + Units[unit];
    }
}
