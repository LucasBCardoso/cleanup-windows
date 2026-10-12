using System.IO.Enumeration;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Scanning;

/// <summary>
/// Big (and optionally stale) files in the profile folder. Always <c>review</c>: these are user documents. NTFS often
/// doesn't update last-access times, so "not opened for N days" uses the later of last access and last write.
/// </summary>
public sealed class LargeOldFilesTask(long minimumSize = LargeOldFilesTask.DefaultMinimumSize, int? olderThanDays = null,
    IReadOnlyList<string>? roots = null, int resultLimit = LargeOldFilesTask.DefaultResultLimit, DateTime? nowUtc = null) : IScanTask
{
    public const long DefaultMinimumSize = 100L * 1000 * 1000;
    public const int DefaultResultLimit = 500;

    /// <summary>Skipped wholesale: app-managed data, already covered by other modules.</summary>
    internal static readonly HashSet<string> SkippedHomeDirectories = new(StringComparer.OrdinalIgnoreCase) { "AppData" };

    private readonly DateTime now = nowUtc ?? DateTime.UtcNow;

    public long MinimumSize => minimumSize;
    public string Id => "large-old-files";
    public string Title => "Arquivos grandes e antigos";
    public ScanCategory Category => ScanCategory.LargeOldFiles;

    public ScanReport Scan(ScanEnvironment environment, SafetyPolicy policy, CancellationToken cancellation)
    {
        var home = environment.Home;
        var rootPaths = roots is { Count: > 0 } ? roots.Select(r => Path.IsPathFullyQualified(r) ? r : Path.Combine(home, r)) : [home];
        var found = new List<ScanItem>();
        var skipped = new List<string>();
        foreach (var root in rootPaths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            Walk(root, home, policy, found, skipped, cancellation);
        }
        var top = found.OrderByDescending(i => i.Size).Take(resultLimit).ToList();
        return new ScanReport(Id, Title, Category, top, skipped);
    }

    private void Walk(string root, string home, SafetyPolicy policy, List<ScanItem> into, List<string> skipped, CancellationToken cancellation)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var minimum = minimumSize;
        var files = new FileSystemEnumerable<(string Path, long Length, DateTime Write, DateTime Access)>(root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, e.LastAccessTimeUtc.UtcDateTime), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && e.Length >= minimum && !FileMetrics.IsCloudOnly(e.Attributes),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                !(e.Directory.Length == home.Length && SkippedHomeDirectories.Contains(e.FileName.ToString())
                  && string.Equals(e.Directory.ToString(), home, StringComparison.OrdinalIgnoreCase))
                && !e.FileName.StartsWith("."),
        };
        var count = 0;
        try
        {
            foreach (var f in files)
            {
                if (++count % 256 == 0) cancellation.ThrowIfCancellationRequested();
                var lastUse = f.Access > f.Write ? f.Access : f.Write;
                if (olderThanDays is { } days && now - lastUse < TimeSpan.FromDays(days)) continue;
                if (!policy.Verdict(f.Path).IsAllowed) continue;
                into.Add(new ScanItem
                {
                    Path = f.Path, Size = f.Length, RuleId = Id, Category = Category, Risk = RiskLevel.Review,
                    LastAccessedUtc = lastUse, LastWriteUtc = f.Write,
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped.Add(root); }
    }
}
