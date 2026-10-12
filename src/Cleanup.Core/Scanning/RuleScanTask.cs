using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Scanning;

/// <summary>
/// Generic, data-driven scanner: expands a rule's path templates and reports each match as one item. Browser caches
/// need review while their browser runs (it has them open).
/// </summary>
public sealed class RuleScanTask(CleanupRule rule, IReadOnlySet<string>? runningProcesses = null, DateTime? nowUtc = null) : IScanTask
{
    public CleanupRule Rule { get; } = rule;
    private readonly IReadOnlySet<string> running = runningProcesses ?? new HashSet<string>();
    private readonly DateTime now = nowUtc ?? DateTime.UtcNow;

    public string Id => Rule.Id;
    public string Title => Rule.Name;
    public ScanCategory Category => Rule.Category;

    /// <summary>Path fragment (vendor folder) → process image name of the browser that owns it.</summary>
    internal static readonly (string Fragment, string Process)[] BrowserOwners =
    [
        (@"\Google\Chrome\", "chrome"), (@"\Microsoft\Edge\", "msedge"), (@"\BraveSoftware\", "brave"),
        (@"\Vivaldi\", "vivaldi"), (@"\Opera Software\", "opera"), (@"\Mozilla\Firefox\", "firefox"),
    ];

    public ScanReport Scan(ScanEnvironment environment, SafetyPolicy policy, CancellationToken cancellation)
    {
        var items = new List<ScanItem>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in Rule.Paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (environment.Expand(template) is not { } pattern) continue;
            var expansion = GlobExpander.Expand(pattern);
            skipped.AddRange(expansion.Unreadable);
            foreach (var path in expansion.Matches)
            {
                if (!seen.Add(path)) continue;
                if (MakeItem(path, policy, cancellation) is { } item) items.Add(item);
            }
        }
        return new ScanReport(Id, Title, Category, [.. items.OrderByDescending(i => i.Size)],
            skipped.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private ScanItem? MakeItem(string path, SafetyPolicy policy, CancellationToken cancellation)
    {
        var name = Path.GetFileName(path);
        if (Rule.Excludes.Any(e => GlobExpander.Matches(name, e))) return null;
        if (!policy.Verdict(path).IsAllowed) return null;
        var modified = FileMetrics.LastWriteUtc(path);
        if (Rule.MinAgeDays is { } days && (modified is null || now - modified.Value < TimeSpan.FromDays(days))) return null;
        cancellation.ThrowIfCancellationRequested();
        var size = FileMetrics.Size(path);
        if (size <= 0) return null;
        var risk = Rule.Risk;
        if (risk == RiskLevel.Safe && BrowserOwners.Any(b => path.Contains(b.Fragment, StringComparison.OrdinalIgnoreCase)
                                                            && running.Contains(b.Process)))
            risk = RiskLevel.Review;
        return new ScanItem
        {
            Path = path, Size = size, RuleId = Rule.Id, Category = Rule.Category, Risk = risk,
            LastAccessedUtc = FileMetrics.LastAccessUtc(path), LastWriteUtc = modified, RequiresAdmin = Rule.RequiresAdmin,
        };
    }
}
