using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Developer;

/// <summary>
/// A regenerable build-output folder: matched by name, then proven by a marker next to it and/or an entry inside it.
/// Markers and inside entries are name globs (<c>*.csproj</c>); any one of each list is enough.
/// </summary>
public sealed record ArtifactRule(IReadOnlyList<string> Names, IReadOnlyList<string> Markers, IReadOnlyList<string> Inside,
    string Ecosystem, string RebuildHint)
{
    private static readonly string[] Gradle = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];
    private static readonly string[] DotNetProjects = ["*.csproj", "*.fsproj", "*.vbproj"];

    public static IReadOnlyList<ArtifactRule> All { get; } =
    [
        new(["node_modules"], ["package.json"], [], "Node", "npm install"),
        new([".next"], ["package.json"], [], "Next.js", "next build"),
        new([".venv", "venv"], [], ["pyvenv.cfg"], "Python", "python -m venv"),
        new(["__pycache__"], [], ["*.pyc"], "Python", "python (recriado ao rodar)"),
        new(["target"], ["Cargo.toml"], [], "Rust", "cargo build"),
        new(["build", ".gradle"], Gradle, [], "Gradle", "gradlew build"),
        new(["Pods"], ["Podfile"], [], "CocoaPods", "pod install"),
        new([".build"], ["Package.swift"], [], "SwiftPM", "swift build"),
        new([".dart_tool", "build"], ["pubspec.yaml"], [], "Flutter", "flutter pub get"),
        // .NET: a project file next to the folder, and the configuration folders (or NuGet's restore output) inside,
        // so a repository's own "bin" folder of scripts next to a .sln is never offered.
        new(["bin"], DotNetProjects, ["Debug", "Release"], ".NET", "dotnet build"),
        new(["obj"], [.. DotNetProjects, "*.sln", "*.slnx"], ["Debug", "Release", "project.assets.json"], ".NET", "dotnet build"),
        // "dist" is only build output when a bundler or a Python build backend proves it.
        new(["dist"], ["vite.config.*", "webpack.config.*", "rollup.config.*", "tsup.config.*", "angular.json"], [], "JavaScript", "npm run build"),
        new(["dist"], ["pyproject.toml", "setup.py"], ["*.whl", "*.tar.gz"], "Python", "python -m build"),
    ];

    internal static readonly HashSet<string> AllNames = new(All.SelectMany(r => r.Names), StringComparer.OrdinalIgnoreCase);

    internal bool Matches(string name, IReadOnlyList<string> siblingNames, Func<IReadOnlyList<string>> insideNames)
    {
        if (!Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return false;
        if (Markers.Count > 0 && !siblingNames.Any(s => Markers.Any(m => GlobExpander.Matches(s, m)))) return false;
        if (Inside.Count > 0 && !insideNames().Any(s => Inside.Any(m => GlobExpander.Matches(s, m)))) return false;
        return true;
    }
}

public sealed record ProjectArtifact(string Path, string ProjectPath, string Ecosystem, long Size, DateTime? LastActivityUtc, string RebuildHint)
{
    public string Id => Path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string ProjectName => System.IO.Path.GetFileName(ProjectPath);

    public ScanItem ToScanItem() => new()
    {
        Path = Path, Size = Size, RuleId = "dev-artifact", Category = ScanCategory.DeveloperJunk, Risk = RiskLevel.Review,
        LastAccessedUtc = LastActivityUtc, LastWriteUtc = LastActivityUtc, DisplayName = $"{ProjectName} › {Name}",
    };
}

/// <param name="PartialRoots">Roots where the entry limit stopped the walk early.</param>
/// <param name="UnreadableRoots">Roots that couldn't be listed (missing or access denied).</param>
public sealed record ArtifactScanResult(IReadOnlyList<ProjectArtifact> Artifacts, IReadOnlyList<string> PartialRoots,
    IReadOnlyList<string> UnreadableRoots);

/// <summary>
/// Walks project folders for stale build output. Never follows links, never enters <c>.git</c> or an artifact, and
/// stops at <c>maxDepth</c> / <c>entryLimit</c> so a huge tree can't hang the scan.
/// </summary>
public sealed class ProjectArtifactScanner(IReadOnlyList<string> roots, int maxDepth = 6, int entryLimit = 200_000, bool measureSize = true)
{
    /// <summary>Home-relative folders where projects usually live on Windows.</summary>
    public static IReadOnlyList<string> CandidateRootNames { get; } =
    [
        @"source\repos", "dev", "Projects", "code", "src", "workspace", "repos", "git", "GitHub", "IdeaProjects",
        "AndroidStudioProjects", "Developer", "Desktop", "Documents",
    ];

    /// <summary>
    /// Existing well-known project folders, plus <paramref name="knownFolders"/> (the real Desktop and Documents,
    /// which OneDrive may have moved). Windows has no privacy prompts, so Desktop and Documents are scanned by default.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(string home, IEnumerable<string>? knownFolders = null) =>
        FolderWalk.Outermost(CandidateRootNames.Select(n => Path.Combine(home, n)).Concat(knownFolders ?? [])
            .Where(FolderWalk.IsRealDirectory));

    public ArtifactScanResult Scan(CancellationToken cancellation = default)
    {
        var artifacts = new List<ProjectArtifact>();
        var partial = new List<string>();
        var unreadable = new List<string>();
        foreach (var root in FolderWalk.Outermost(roots))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!FolderWalk.IsRealDirectory(root) || FolderWalk.List(root) is null)
            {
                unreadable.Add(root);
                continue;
            }
            var budget = entryLimit;
            if (!Walk(root, root, 0, ref budget, artifacts, cancellation)) partial.Add(root);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = artifacts.Where(a => seen.Add(a.Path)).OrderByDescending(a => a.Size).ToList();
        return new ArtifactScanResult(unique, partial, unreadable);
    }

    /// <summary>False when the entry budget ran out.</summary>
    private bool Walk(string dir, string root, int depth, ref int budget, List<ProjectArtifact> into, CancellationToken cancellation)
    {
        if (depth > maxDepth) return true;
        cancellation.ThrowIfCancellationRequested();
        var entries = FolderWalk.List(dir) ?? [];
        budget -= entries.Count;
        if (budget < 0) return false;
        var names = entries.Select(e => e.Name).ToList();
        foreach (var entry in entries)
        {
            if (entry is not DirectoryInfo child || child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (FolderWalk.IsLink(child)) continue;
            var rule = ArtifactRule.All.FirstOrDefault(r => r.Matches(child.Name, names, () => ListNames(child.FullName)));
            if (rule is not null)
            {
                into.Add(new ProjectArtifact(child.FullName, dir, rule.Ecosystem,
                    measureSize ? FileMetrics.DirectorySize(child.FullName, cancellation) : 0, LastActivity(dir, root), rule.RebuildHint));
                continue;
            }
            if (child.Name.StartsWith('.')) continue;
            if (!Walk(child.FullName, root, depth + 1, ref budget, into, cancellation)) return false;
        }
        return true;
    }

    private static List<string> ListNames(string dir) => FolderWalk.List(dir)?.Select(e => e.Name).ToList() ?? [];

    /// <summary>Entries examined per project when looking for its newest change.</summary>
    internal const int ActivityEntryLimit = 20_000;

    /// <summary>
    /// Newest modification anywhere in the project (artifact folders, hidden folders and links excluded, at most
    /// <see cref="ActivityEntryLimit"/> entries), plus <c>.git\HEAD</c> and <c>.git\index</c> of the nearest repository at or
    /// above the project — up to the scan root — since commits and staging are activity.
    /// </summary>
    public static DateTime? LastActivity(string project, string? root = null)
    {
        DateTime? newest = null;
        void Consider(DateTime date)
        {
            if (newest is null || date > newest) newest = date;
        }
        var queue = new Stack<string>();
        queue.Push(project);
        var budget = ActivityEntryLimit;
        while (budget > 0 && queue.TryPop(out var dir))
        {
            var entries = FolderWalk.List(dir) ?? [];
            budget -= entries.Count;
            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || ArtifactRule.AllNames.Contains(entry.Name) || FolderWalk.IsLink(entry)) continue;
                Consider(entry.LastWriteTimeUtc);
                if (entry is DirectoryInfo) queue.Push(entry.FullName);
            }
        }
        if (NearestRepository(project, root) is { } repo)
        {
            foreach (var name in (string[])["HEAD", "index"])
            {
                var path = Path.Combine(repo, ".git", name);
                if (File.Exists(path)) Consider(File.GetLastWriteTimeUtc(path));
            }
        }
        return newest;
    }

    /// <summary>The closest folder at or above <paramref name="project"/> (not above <paramref name="root"/>) that contains <c>.git</c>.</summary>
    internal static string? NearestRepository(string project, string? root)
    {
        var current = PathUtil.Normalize(project);
        var stop = root is null ? null : PathUtil.Normalize(root);
        while (true)
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return current;
            if ((stop is not null && PathUtil.SameOrdinalIgnoreCase(current, stop)) || Path.GetDirectoryName(current) is not { } parent) return null;
            current = parent;
            if (stop is not null && !SafetyPolicy.IsWithin(current, stop)) return null;
        }
    }

    /// <summary>Artifacts whose project hasn't changed in <paramref name="olderThanDays"/> (null = all).</summary>
    public static IReadOnlyList<ProjectArtifact> Stale(IEnumerable<ProjectArtifact> artifacts, int? olderThanDays, DateTime nowUtc)
    {
        if (olderThanDays is not { } days) return artifacts.ToList();
        var cutoff = nowUtc.AddDays(-days);
        return artifacts.Where(a => (a.LastActivityUtc ?? DateTime.MinValue) < cutoff).ToList();
    }
}
