using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Tests.Scanning;

public class ScanningTests : IDisposable
{
    private readonly TempHome home = new();
    public void Dispose() => home.Dispose();

    private static CleanupRule Rule(string id, params string[] paths) => new()
    {
        Id = id, Name = id, Category = ScanCategory.SystemJunk, Paths = paths,
    };

    [Fact]
    public void GlobMatchesCaseInsensitivelyWithClasses()
    {
        Assert.True(GlobExpander.Matches("thumbcache_256.db", "thumbcache_*.db"));
        Assert.True(GlobExpander.Matches("THUMBCACHE_1.DB", "thumbcache_?.db"));
        Assert.True(GlobExpander.Matches("b", "[a-c]"));
        Assert.False(GlobExpander.Matches("d", "[a-c]"));
        Assert.False(GlobExpander.Matches("a", "[!a]"));
        Assert.False(GlobExpander.Matches("thumbcache_1.db.bak", "thumbcache_*.db"));
    }

    [Fact]
    public void ExpandWalksComponentsAndSkipsDotEntries()
    {
        home.File(@"AppData\Local\Google\Chrome\User Data\Default\Cache\f1");
        home.File(@"AppData\Local\Google\Chrome\User Data\Profile 1\Cache\f2");
        home.File(@"AppData\Local\Google\Chrome\User Data\.hidden\Cache\f3");
        var result = GlobExpander.Expand(Path.Combine(home.Home, @"AppData\Local\Google\Chrome\User Data\*\Cache\*"));
        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.DoesNotContain(".hidden", m));
    }

    [Fact]
    public void ExpandNeverStepsThroughAJunction()
    {
        var target = home.Dir(@"Documents\precious");
        home.File(@"Documents\precious\thesis.docx");
        var temp = home.Dir(@"AppData\Local\Temp");
        var link = Path.Combine(temp, "link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (IOException) { return; } // symlinks need Developer Mode or admin; the junction case is covered elsewhere
        catch (UnauthorizedAccessException) { return; }
        var result = GlobExpander.Expand(Path.Combine(temp, @"*\*"));
        Assert.Empty(result.Matches);
        // The link itself can be matched (and is then removed as a link), but it counts as no space.
        Assert.Equal(0, FileMetrics.Size(link));
    }

    [Fact]
    public void RuleScanReportsSizesAndHonoursAge()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        home.File(@"AppData\Local\Temp\old.tmp", 100, old);
        home.File(@"AppData\Local\Temp\new.tmp", 50);
        var rule = Rule("user-temp", @"%TEMP%\*", @"%LOCALAPPDATA%\Temp\*") with { MinAgeDays = 1 };
        var report = new RuleScanTask(rule).Scan(home.Environment, home.Policy(), CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.EndsWith("old.tmp", item.Path);
        Assert.Equal(100, item.Size);
    }

    [Fact]
    public void RuleScanSkipsUnknownVariablesAndExcludes()
    {
        home.File(@"AppData\Local\CrashDumps\a.dmp");
        home.File(@"AppData\Local\CrashDumps\keep.dmp");
        var rule = Rule("x", @"%NOPE%\*", @"%LOCALAPPDATA%\CrashDumps\*") with { Excludes = ["keep*"] };
        var report = new RuleScanTask(rule).Scan(home.Environment, home.Policy(), CancellationToken.None);
        Assert.EndsWith("a.dmp", Assert.Single(report.Items).Path);
    }

    [Fact]
    public void BrowserCachesNeedReviewWhileTheBrowserRuns()
    {
        home.File(@"AppData\Local\Google\Chrome\User Data\Default\Cache\data_1", 100);
        var rule = DefaultRules.All.Single(r => r.Id == "browser-caches");
        var idle = new RuleScanTask(rule, new HashSet<string>()).Scan(home.Environment, home.Policy(), CancellationToken.None);
        var running = new RuleScanTask(rule, new HashSet<string> { "chrome" }).Scan(home.Environment, home.Policy(), CancellationToken.None);
        Assert.Equal(RiskLevel.Safe, Assert.Single(idle.Items).Risk);
        Assert.Equal(RiskLevel.Review, Assert.Single(running.Items).Risk);
    }

    [Fact]
    public void ChromiumCachesAreProvenByStructure()
    {
        home.File(@"AppData\Roaming\Slack\Cache\Cache_Data\f_000001", 200);
        home.File(@"AppData\Roaming\Slack\Code Cache\js\index", 100);
        home.File(@"AppData\Roaming\SomeTool\Cache\settings.json", 100);          // not Chromium's
        home.File(@"AppData\Roaming\discord\Service Worker\CacheStorage\x", 50);  // always review
        home.File(@"AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f", 50); // browser rule's job
        var task = new ChromiumCacheTask(new HashSet<string> { "slack" });
        var report = task.Scan(home.Environment, home.Policy(), CancellationToken.None);
        Assert.Equal(3, report.Items.Count);
        Assert.DoesNotContain(report.Items, i => i.Path.Contains("SomeTool") || i.Path.Contains("Chrome"));
        Assert.All(report.Items, i => Assert.Equal(RiskLevel.Review, i.Risk)); // Slack runs; CacheStorage always review
    }

    [Fact]
    public void LargeFilesAreReviewAndSkipAppData()
    {
        home.File(@"Videos\movie.mkv", 2000);
        home.File(@"AppData\Local\big.bin", 5000);
        home.File(@"Documents\small.txt", 10);
        var report = new LargeOldFilesTask(minimumSize: 1000).Scan(home.Environment, home.Policy(), CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.EndsWith("movie.mkv", item.Path);
        Assert.Equal(RiskLevel.Review, item.Risk);
    }

    [Fact]
    public void LargeFilesOlderThanUsesLastUse()
    {
        var path = home.File(@"Videos\old.mkv", 2000, DateTime.UtcNow.AddDays(-400));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-400));
        home.File(@"Videos\new.mkv", 2000);
        var report = new LargeOldFilesTask(minimumSize: 1000, olderThanDays: 180).Scan(home.Environment, home.Policy(), CancellationToken.None);
        Assert.EndsWith("old.mkv", Assert.Single(report.Items).Path);
    }

    private sealed class Boom : IScanTask
    {
        public string Id => "boom";
        public string Title => "Boom";
        public ScanCategory Category => ScanCategory.SystemJunk;
        public ScanReport Scan(ScanEnvironment e, Cleanup.Core.Safety.SafetyPolicy p, CancellationToken c) => throw new InvalidOperationException("kaput");
    }

    [Fact]
    public async Task AFailingTaskNeverHidesTheOthers()
    {
        home.File(@"AppData\Local\CrashDumps\a.dmp");
        var engine = new ScanEngine(home.Environment, home.Policy());
        var outcomes = await engine.RunAsync([new Boom(), new RuleScanTask(Rule("dumps", @"%LOCALAPPDATA%\CrashDumps\*"))]);
        Assert.IsType<ScanOutcome.Failure>(outcomes[0]);
        Assert.Single(outcomes[1].Report!.Items);
    }

    [Fact]
    public void SmartScanUsesDedicatedTasksForCodeBasedRules()
    {
        var tasks = ScanEngine.SmartScanTasks(DefaultRules.All, new HashSet<string>());
        Assert.DoesNotContain(tasks, t => t is RuleScanTask r && DefaultRules.CodeBasedRuleIds.Contains(r.Rule.Id));
        Assert.Contains(tasks, t => t is ChromiumCacheTask);
    }
}
