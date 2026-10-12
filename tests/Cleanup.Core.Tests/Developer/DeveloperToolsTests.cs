using Cleanup.Core.Developer;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Tests.Developer;

/// <summary>Records calls and answers with stubs; never runs anything.</summary>
public sealed class FakeTools : IToolRunner
{
    private readonly Dictionary<string, ToolResult> stubs = [];
    public List<List<string>> Calls { get; } = [];

    public void Stub(IReadOnlyList<string> arguments, string stdout = "", string stderr = "", int exitCode = 0) =>
        stubs[string.Join("\u0001", arguments)] = new ToolResult(exitCode, stdout, stderr);

    public ToolResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        Calls.Add([.. arguments]);
        return stubs.TryGetValue(string.Join("\u0001", arguments), out var result) ? result : new ToolResult(1, "", "not stubbed");
    }
}

public static class DevSamples
{
    public const string DockerDf = """
        {"Active":"2","Reclaimable":"1.2GB (50%)","Size":"2.4GB","TotalCount":"5","Type":"Images"}
        {"Active":"0","Reclaimable":"0B","Size":"0B","TotalCount":"0","Type":"Containers"}
        {"Active":"1","Reclaimable":"0B (0%)","Size":"100MB","TotalCount":"1","Type":"Local Volumes"}
        {"Active":"0","Reclaimable":"512MB","Size":"512MB","TotalCount":"10","Type":"Build Cache"}
        """;
}

public class DeveloperToolsTests
{
    private static DeveloperTools Tools(FakeTools fake) => new(fake, @"C:\Program Files\Docker\Docker\resources\bin\docker.exe");

    [Fact]
    public void ParsesDockerDiskUsage()
    {
        var usage = DockerParser.Parse(DevSamples.DockerDf.Replace("\n", "\r\n"));
        Assert.Equal(["Images", "Containers", "Local Volumes", "Build Cache"], usage.Select(u => u.Type));
        Assert.Equal("1.2GB (50%)", usage[0].Reclaimable);
        Assert.Equal(10, usage[3].Count);
        Assert.Empty(DockerParser.Parse("garbage\n"));
    }

    [Fact]
    public void DockerNotRunningIsAClearError()
    {
        var fake = new FakeTools();
        fake.Stub(["system", "df", "--format", "{{json .}}"],
            stderr: "error during connect: Get \"http://%2F%2F.%2Fpipe%2Fdocker_engine/v1.47/system/df\": open //./pipe/docker_engine: The system cannot find the file specified.",
            exitCode: 1);
        var error = Assert.Throws<DeveloperToolException>(() => Tools(fake).DockerUsage());
        Assert.Contains("não está em execução", error.Message);
    }

    [Fact]
    public void DockerDaemonErrorsAreNotReportedAsNotRunning()
    {
        var fake = new FakeTools();
        fake.Stub(["system", "df", "--format", "{{json .}}"], stderr: "Error response from daemon: permission denied", exitCode: 1);
        Assert.Equal("Error response from daemon: permission denied", Assert.Throws<DeveloperToolException>(() => Tools(fake).DockerUsage()).Message);
    }

    [Fact]
    public void DockerPrunesNeverTouchVolumes()
    {
        var fake = new FakeTools();
        fake.Stub(["builder", "prune", "-f"]);
        fake.Stub(["image", "prune", "-f"]);
        Tools(fake).PruneDockerBuildCache();
        Tools(fake).PruneDanglingImages();
        Assert.Equal([["builder", "prune", "-f"], ["image", "prune", "-f"]], fake.Calls);
        Assert.DoesNotContain(fake.Calls.SelectMany(c => c), a => a.Contains("volume") || a == "-a" || a == "--all" || a == "system");
    }

    [Fact]
    public void WithoutDockerNothingRuns()
    {
        var fake = new FakeTools();
        var tools = new DeveloperTools(fake, null);
        Assert.Empty(tools.DockerUsage());
        tools.PruneDanglingImages();
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void DetectsDockerOnPathOrInDockerDesktop()
    {
        var env = new ScanEnvironment(@"C:\Users\me", new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows;relative\dir;C:\tools", ["ProgramFiles"] = @"C:\Program Files",
        });
        Assert.Equal(@"C:\tools\docker.exe", DeveloperTools.DetectDocker(env, p => p == @"C:\tools\docker.exe"));
        Assert.Equal(@"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
            DeveloperTools.DetectDocker(env, p => p.StartsWith(@"C:\Program Files\", StringComparison.Ordinal)));
        Assert.Null(DeveloperTools.DetectDocker(env, _ => false));
    }

    [Fact]
    public void WslDisksAreReportedBySize()
    {
        using var t = new TempHome();
        var ubuntu = t.File(@"AppData\Local\Packages\Canonical.Ubuntu\LocalState\ext4.vhdx", 300);
        t.File(@"AppData\Local\Docker\wsl\disk\docker_data.vhdx", 500);
        var disks = DeveloperTools.WslDisks(t.Environment, [("Ubuntu", Path.GetDirectoryName(ubuntu)!), ("Gone", @"C:\nowhere"), ("Bad", "relative")]);
        Assert.Equal(["Docker Desktop (dados)", "Ubuntu"], disks.Select(d => d.Name));
        Assert.Equal(300, disks[1].Size);
    }

    [Fact]
    public void DeveloperCachesAreFoundAndReviewedWhereProjectsDependOnThem()
    {
        using var t = new TempHome();
        t.File(@"AppData\Local\npm-cache\_cacache\index-v5\x", 100);
        t.File(@".nuget\packages\newtonsoft.json\13.0.1\lib.dll", 100);
        t.File(@"AppData\Local\Android\Sdk\system-images\android-34\google_apis\x86_64\system.img", 100);
        t.File(@"AppData\Local\JetBrains\Rider2025.1\caches\x", 100);
        var policy = t.Policy();
        var items = DeveloperCaches.Rules.SelectMany(r => new RuleScanTask(r).Scan(t.Environment, policy, CancellationToken.None).Items).ToList();
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.Equal(ScanCategory.DeveloperJunk, i.Category));
        Assert.Equal(RiskLevel.Safe, items.Single(i => i.RuleId == "npm-cache").Risk);
        Assert.Equal(RiskLevel.Review, items.Single(i => i.RuleId == "nuget-packages").Risk);
        var image = items.Single(i => i.RuleId == DeveloperCaches.AndroidSystemImagesId);
        Assert.Equal(RiskLevel.Review, image.Risk);
        Assert.EndsWith(@"android-34\google_apis", image.Path);
        Assert.Equal(DeveloperCaches.Rules.Count, DeveloperCaches.Rules.Select(r => r.Id).Distinct().Count());
    }
}
