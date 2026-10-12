using Cleanup.Core.Developer;
using Cleanup.Core.Rules;
using Cleanup.Core.Tests.Duplicates;

namespace Cleanup.Core.Tests.Developer;

/// <summary>Builds project trees under a TempHome's <c>Projects</c> folder.</summary>
public sealed class ProjectTree : IDisposable
{
    public TempHome T { get; } = new();
    public string Root => Path.Combine(T.Home, "Projects");

    public ProjectTree() => Directory.CreateDirectory(Root);

    public string File(string relative, DateTime? modifiedUtc = null, int bytes = 1024) =>
        T.File(Path.Combine(Root, relative), bytes, modifiedUtc);

    public void Touch(string relative, DateTime utc) => Directory.SetLastWriteTimeUtc(Path.Combine(Root, relative), utc);

    public ProjectArtifactScanner Scanner(int maxDepth = 6, int entryLimit = 200_000) => new([Root], maxDepth, entryLimit);

    public void Dispose() => T.Dispose();
}

public class ProjectArtifactTests
{
    private readonly DateTime now = DateTime.UtcNow;
    private DateTime Old => now.AddDays(-200);

    private static List<string> Names(ArtifactScanResult result) =>
        result.Artifacts.Select(a => $"{a.ProjectName}/{a.Name}").Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void FindsEveryEcosystemWithItsMarker()
    {
        using var p = new ProjectTree();
        p.File(@"web\package.json"); p.File(@"web\node_modules\left-pad\index.js");
        p.File(@"web\.next\cache\x");
        p.File(@"api\pyvenv-project\main.py"); p.File(@"api\pyvenv-project\.venv\pyvenv.cfg");
        p.File(@"api\pyvenv-project\__pycache__\main.cpython-312.pyc");
        p.File(@"rusty\Cargo.toml"); p.File(@"rusty\target\debug\app");
        p.File(@"droid\build.gradle.kts"); p.File(@"droid\build\out.apk"); p.File(@"droid\.gradle\cache");
        p.File(@"ios\Podfile"); p.File(@"ios\Pods\Alamofire\a.swift");
        p.File(@"pkg\Package.swift"); p.File(@"pkg\.build\debug\x");
        p.File(@"flutter\pubspec.yaml"); p.File(@"flutter\.dart_tool\x"); p.File(@"flutter\build\app");
        p.File(@"dotnet\App.csproj"); p.File(@"dotnet\bin\Debug\net10.0\App.dll"); p.File(@"dotnet\obj\project.assets.json");
        p.File(@"vite\package.json"); p.File(@"vite\vite.config.ts"); p.File(@"vite\dist\index.html");
        p.File(@"wheel\pyproject.toml"); p.File(@"wheel\dist\pkg-1.0-py3-none-any.whl");
        Assert.Equal(((string[])
        [
            "api/__pycache__", "api/.venv", "dotnet/bin", "dotnet/obj", "droid/.gradle", "droid/build", "flutter/.dart_tool",
            "flutter/build", "ios/Pods", "pkg/.build", "rusty/target", "vite/dist", "web/.next", "web/node_modules", "wheel/dist",
        ]).Order(StringComparer.Ordinal), Names(p.Scanner().Scan()).Select(n => n.Replace("pyvenv-project", "api")).Order(StringComparer.Ordinal));
        var dotnet = p.Scanner().Scan().Artifacts.First(a => a.Name == "bin");
        Assert.Equal(".NET", dotnet.Ecosystem);
        Assert.Equal("dotnet build", dotnet.RebuildHint);
    }

    [Fact]
    public void ArtifactNamesWithoutMarkersAreIgnored()
    {
        using var p = new ProjectTree();
        p.File(@"notes\build\report.pdf");          // no Gradle/Dart marker
        p.File(@"game\target\aim.txt");             // no Cargo.toml
        p.File(@"loose\node_modules\x.js");         // no package.json
        p.File(@"py\venv\readme.txt");              // venv without pyvenv.cfg
        p.File(@"site\package.json"); p.File(@"site\dist\app.js");    // dist without a bundler config
        p.File(@"repo\App.sln"); p.File(@"repo\bin\deploy.ps1");      // a scripts folder next to a solution
        p.File(@"proj\App.csproj"); p.File(@"proj\bin\tool.exe");     // bin without Debug/Release
        Assert.Empty(p.Scanner().Scan().Artifacts);
    }

    [Fact]
    public void NestedArtifactsAreCountedOnceAndSizeIncludesThem()
    {
        using var p = new ProjectTree();
        p.File(@"web\package.json");
        p.File(@"web\node_modules\a\package.json");
        p.File(@"web\node_modules\a\node_modules\b\index.js", bytes: 50_000);
        var result = p.Scanner().Scan();
        Assert.Equal(["web/node_modules"], Names(result));
        Assert.True(result.Artifacts[0].Size >= 50_000);
        Assert.Equal("Node", result.Artifacts[0].Ecosystem);
    }

    [Fact]
    public void JunctionsAndGitAreNotFollowed()
    {
        using var p = new ProjectTree();
        p.File(@"web\package.json");
        var elsewhere = Path.Combine(p.T.Root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "x"));
        System.IO.File.WriteAllText(Path.Combine(elsewhere, "x", "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(elsewhere, "x", "node_modules"));
        Assert.True(TestFiles.Junction(Path.Combine(p.Root, "web", "home-link"), elsewhere));
        Assert.True(TestFiles.Junction(Path.Combine(p.Root, "web", "node_modules"), elsewhere)); // a linked artifact is not offered
        p.File(@"repo\.git\modules\x\package.json"); p.File(@"repo\.git\modules\x\node_modules\y");
        Assert.Empty(p.Scanner().Scan().Artifacts);
    }

    [Fact]
    public void DepthAndEntryCapsStopTheWalk()
    {
        using var p = new ProjectTree();
        p.File(@"a\b\c\d\e\f\g\package.json"); p.File(@"a\b\c\d\e\f\g\node_modules\x");
        Assert.Empty(p.Scanner(maxDepth: 4).Scan().Artifacts);
        Assert.Single(p.Scanner(maxDepth: 8).Scan().Artifacts);
        for (var i = 0; i < 30; i++) p.File($@"wide\f{i}.txt");
        Assert.Equal([p.Root], p.Scanner(entryLimit: 10).Scan().PartialRoots);
    }

    [Fact]
    public void LastActivityIgnoresArtifactsAndUsesGit()
    {
        using var p = new ProjectTree();
        p.File(@"web\package.json", Old);
        p.File(@"web\src\app.js", Old);
        p.File(@"web\node_modules\fresh.js", now);   // artifact contents don't count
        p.Touch(@"web\src", Old);
        p.Touch(@"web\node_modules", now);
        var artifact = Assert.Single(p.Scanner().Scan().Artifacts);
        Assert.True(Math.Abs(((artifact.LastActivityUtc ?? now) - Old).TotalSeconds) < 2);
        Assert.Single(ProjectArtifactScanner.Stale([artifact], 90, now));

        p.File(@"web\.git\index", now);
        artifact = Assert.Single(p.Scanner().Scan().Artifacts);
        Assert.Empty(ProjectArtifactScanner.Stale([artifact], 90, now));
        Assert.Single(ProjectArtifactScanner.Stale([artifact], null, now));
    }

    [Fact]
    public void GitActivityIsFoundAboveTheArtifactsParent()
    {
        using var p = new ProjectTree();
        p.File(@"MyApp\app\build.gradle", Old);
        p.File(@"MyApp\app\build\outputs\app.apk", Old);
        p.File(@"MyApp\settings.gradle", Old);
        p.Touch(@"MyApp\app", Old);
        p.File(@"MyApp\.git\index", now);
        var artifact = p.Scanner().Scan().Artifacts.First(a => a.Name == "build");
        Assert.Empty(ProjectArtifactScanner.Stale([artifact], 90, now));
    }

    [Fact]
    public void DeepSourceEditsCountAsActivity()
    {
        using var p = new ProjectTree();
        p.File(@"droid\build.gradle", Old);
        p.File(@"droid\build\out.apk", Old);
        p.File(@"droid\src\main\java\com\acme\App.kt", now);
        foreach (var dir in (string[])[@"droid\src\main\java\com\acme", @"droid\src\main\java\com", @"droid\src\main\java", @"droid\src\main", @"droid\src"])
            p.Touch(dir, Old);
        var artifact = Assert.Single(p.Scanner().Scan().Artifacts);
        Assert.Empty(ProjectArtifactScanner.Stale([artifact], 90, now));
    }

    [Fact]
    public void OverlappingRootsCountEachArtifactOnce()
    {
        using var p = new ProjectTree();
        p.File(@"web\package.json"); p.File(@"web\node_modules\x.js");
        var scanner = new ProjectArtifactScanner([p.Root, Path.Combine(p.Root, "web"), p.Root.ToUpperInvariant()]);
        Assert.Single(scanner.Scan().Artifacts);
    }

    [Fact]
    public void UnreadableRootsAreReported()
    {
        using var p = new ProjectTree();
        var missing = Path.Combine(p.T.Home, "Gone");
        Assert.Equal([missing], new ProjectArtifactScanner([p.Root, missing]).Scan().UnreadableRoots);
    }

    [Fact]
    public void DefaultRootsAreTheUsualProjectFolders()
    {
        using var t = new TempHome();
        foreach (var name in (string[])["Projects", @"source\repos", "dev", "Desktop", "Downloads", "Music"]) Directory.CreateDirectory(Path.Combine(t.Home, name));
        var extra = Path.Combine(t.Root, "OneDrive", "Documents");
        Directory.CreateDirectory(extra);
        var roots = ProjectArtifactScanner.DefaultRoots(t.Home, [extra]).Select(r => Path.GetRelativePath(t.Root, r)).Order().ToList();
        Assert.Equal([@"OneDrive\Documents", @"Users\me\Desktop", @"Users\me\dev", @"Users\me\Projects", @"Users\me\source\repos"], roots);
    }

    [Fact]
    public void ArtifactsBecomeReviewScanItems()
    {
        using var p = new ProjectTree();
        p.File(@"rusty\Cargo.toml"); p.File(@"rusty\target\x");
        var item = Assert.Single(p.Scanner().Scan().Artifacts).ToScanItem();
        Assert.Equal(RiskLevel.Review, item.Risk);
        Assert.Equal(ScanCategory.DeveloperJunk, item.Category);
        Assert.Equal("rusty › target", item.Name);
        Assert.True(p.T.Policy().Verdict(item.Path).IsAllowed);
    }
}
