using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Scanning;

/// <summary>
/// Caches that Chromium-based apps (Electron apps like Slack, Discord, VS Code, Teams…) keep in <c>%APPDATA%</c> and
/// <c>%LOCALAPPDATA%</c>. A folder is listed only when its structure proves it is Chromium's (e.g.
/// <c>Cache\Cache_Data</c>), so an unrelated folder called "Cache" stays. Caches of running apps need review.
/// Browsers are covered by the <c>browser-caches</c> rule and skipped here.
/// </summary>
public sealed class ChromiumCacheTask(IReadOnlySet<string> runningProcesses) : IScanTask
{
    public const string RuleId = "chromium-app-caches";

    /// <summary>Cache folder (relative to an app or profile folder) → entries, any one of which proves it is Chromium's.</summary>
    internal static readonly (string Folder, string[] AnyOf)[] Markers =
    [
        ("Cache", ["Cache_Data"]),
        ("Code Cache", ["js", "wasm"]),
        ("GPUCache", ["index"]),
        ("GrShaderCache", ["index"]),
        ("ShaderCache", ["index"]),
        ("DawnCache", ["index"]),
        ("DawnGraphiteCache", ["index"]),
        ("DawnWebGPUCache", ["index"]),
        (@"Service Worker\CacheStorage", []),
    ];

    /// <summary>Offline data of web apps (the Cache API): site data rather than cache, never pre-selected.</summary>
    internal static readonly HashSet<string> AlwaysReview = new(StringComparer.OrdinalIgnoreCase) { @"Service Worker\CacheStorage" };

    /// <summary>Vendor folders the browser rule already covers.</summary>
    internal static readonly HashSet<string> BrowserVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google", "Microsoft", "BraveSoftware", "Vivaldi", "Opera Software", "Mozilla", "Chromium",
    };

    private readonly HashSet<string> running = runningProcesses.Select(Normalize).ToHashSet();

    public string Id => RuleId;
    public string Title => "Caches de apps (Chromium/Electron)";
    public ScanCategory Category => ScanCategory.SystemJunk;

    /// <summary>Lowercase letters and digits only: "Microsoft Teams" and "msteams" stay distinct, "Slack" == "slack".</summary>
    internal static string Normalize(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public ScanReport Scan(ScanEnvironment environment, SafetyPolicy policy, CancellationToken cancellation)
    {
        var items = new List<ScanItem>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var support in (string[])[environment.RoamingAppData, environment.LocalAppData])
        {
            if (!Directory.Exists(support)) continue;
            var apps = Subdirectories(support);
            if (apps is null) { skipped.Add(support); continue; }
            foreach (var app in apps)
            {
                cancellation.ThrowIfCancellationRequested();
                if (BrowserVendors.Contains(Path.GetFileName(app))) continue;
                foreach (var (container, owners) in Containers(app))
                {
                    var isRunning = owners.Any(running.Contains);
                    foreach (var (cache, folder) in Caches(container))
                    {
                        if (!seen.Add(cache) || !policy.Verdict(cache).IsAllowed) continue;
                        var size = FileMetrics.DirectorySize(cache, cancellation);
                        if (size <= 0) continue;
                        items.Add(new ScanItem
                        {
                            Path = cache, Size = size, RuleId = Id, Category = Category,
                            Risk = isRunning || AlwaysReview.Contains(folder) ? RiskLevel.Review : RiskLevel.Safe,
                            LastWriteUtc = FileMetrics.LastWriteUtc(cache),
                        });
                    }
                }
            }
        }
        return new ScanReport(Id, Title, Category, [.. items.OrderByDescending(i => i.Size)], skipped);
    }

    /// <summary>
    /// Folders that may hold caches, with the normalized names of the app they belong to: <c>&lt;App&gt;</c>,
    /// <c>&lt;App&gt;\&lt;Sub&gt;</c> (vendor\app or a profile) and <c>…\Default</c>, <c>…\Profile N</c>.
    /// </summary>
    internal static List<(string, string[])> Containers(string app)
    {
        var appName = Normalize(Path.GetFileName(app));
        var result = new List<(string, string[])> { (app, [appName]) };
        foreach (var sub in Subdirectories(app) ?? [])
        {
            var subName = Path.GetFileName(sub);
            string[] owners = [appName, Normalize(subName), Normalize(Path.GetFileName(app) + subName)];
            result.Add((sub, owners));
            foreach (var profile in Subdirectories(sub) ?? [])
            {
                if (IsProfile(Path.GetFileName(profile))) result.Add((profile, owners));
            }
        }
        return result;
    }

    internal static bool IsProfile(string name) =>
        name == "Default" || (name.StartsWith("Profile ", StringComparison.Ordinal) && int.TryParse(name[8..], out _));

    internal static IEnumerable<(string Path, string Folder)> Caches(string container)
    {
        foreach (var (folder, anyOf) in Markers)
        {
            var path = Path.Combine(container, folder);
            if (!GlobExpander.IsRealDirectory(path)) continue;
            if (anyOf.Length == 0 || anyOf.Any(m => File.Exists(Path.Combine(path, m)) || Directory.Exists(Path.Combine(path, m))))
                yield return (path, folder);
        }
    }

    private static List<string>? Subdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
