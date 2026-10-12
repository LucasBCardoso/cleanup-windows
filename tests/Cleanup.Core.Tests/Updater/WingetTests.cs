using Cleanup.Core.Tests.Shared;
using Cleanup.Core.Updater;

namespace Cleanup.Core.Tests.Updater;

/// <summary>Records calls instead of starting winget.</summary>
internal sealed class FakeToolRunner : IToolRunner
{
    public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Func<IReadOnlyList<string>, ToolResult> Respond { get; set; } = _ => new ToolResult(0, "", "");
    public TimeSpan Delay { get; set; }
    public bool Missing { get; set; }
    private int running;
    public int MaxConcurrent { get; private set; }

    public async Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Missing) throw new ToolNotFoundException("winget.exe não pôde ser iniciado");
        lock (Calls) Calls.Add((executable, arguments.ToList()));
        var now = Interlocked.Increment(ref running);
        lock (Calls) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return Respond(arguments);
        }
        finally { Interlocked.Decrement(ref running); }
    }
}

public class WingetUpgradeParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(SharedFiles.RepoRoot, "tests", "Cleanup.Core.Tests", "Fixtures", "winget", name));

    [Fact]
    public void ParsesRecordedPortugueseOutputWithTheExplicitTargetingSection()
    {
        var packages = WingetUpgradeParser.Parse(Fixture("upgrade-pt-BR-v1.29.txt"));
        Assert.Equal(16, packages.Count);
        var assistant = packages[0];
        Assert.Equal("Assistente de Instalação do Windows 11", assistant.Name);
        Assert.Equal("Microsoft.WindowsInstallationAssistant", assistant.Id);
        Assert.Equal("1.4.19041.6448", assistant.Version);
        Assert.Equal("1.4.19041.7779", assistant.Available);
        Assert.Equal("winget", assistant.Source);
        Assert.False(assistant.RequiresExplicitTargeting);

        var runtime = packages.Single(p => p.Id == "Microsoft.DotNet.DesktopRuntime.6");
        Assert.Equal("Microsoft Windows Desktop Runtime - 6.0.11 (x64)", runtime.Name);
        Assert.Equal("Unknown", packages.Single(p => p.Id == "EPSON.EpsonScan2").Version);
        Assert.Equal("v0.2026.09.30.08.29.stable_01", packages.Single(p => p.Id == "Warp.Warp").Available);

        var discord = packages[^1];
        Assert.Equal(("Discord", "Discord.Discord", "1.0.9259", "1.0.9261", "winget"), (discord.Name, discord.Id, discord.Version, discord.Available, discord.Source));
        Assert.True(discord.RequiresExplicitTargeting);
        Assert.DoesNotContain(packages, p => p.Name.Contains("atualizações", StringComparison.Ordinal));
    }

    private const string English =
        "\r   - \r   \\ \r   | \r   / \r" +
        "Name                               Id                           Version        Available      Source\r\n" +
        "-----------------------------------------------------------------------------------------------------\r\n" +
        "Mozilla Firefox (x64 en-US)        Mozilla.Firefox              130.0          131.0.2        winget\r\n" +
        "A Very Long Application Name Tha…  SomeVendor.SomeVeryLongPack… 1.0            2.0            winget\r\n" +
        "\u4F60\u597D\u5DE5\u5177                           Vendor.NiHao                 < 1.2          1.3            winget\r\n" +
        "Spotify Music                      9NCBCSZSJRSB                 1.2.40         1.2.47         msstore\r\n" +
        "4 upgrades available.\r\n" +
        "\r\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\r\n" +
        "Name   Id          Version Available Source\r\n" +
        "-------------------------------------------\r\n" +
        "Pinned Pin.Package 1.0     1.1       winget\r\n";

    [Fact]
    public void ToleratesSpinnerTruncationWideCharactersAndFooters()
    {
        var packages = WingetUpgradeParser.Parse(English);
        Assert.Equal(["Mozilla.Firefox", "SomeVendor.SomeVeryLongPack…", "Vendor.NiHao", "9NCBCSZSJRSB", "Pin.Package"], packages.Select(p => p.Id));
        Assert.True(packages[1].IsIdTruncated);
        Assert.Equal("A Very Long Application Name Tha…", packages[1].Name);
        Assert.Equal("\u4F60\u597D\u5DE5\u5177", packages[2].Name);
        Assert.Equal("< 1.2", packages[2].Version);
        Assert.Equal("msstore", packages[3].Source);
        Assert.True(packages[4].RequiresExplicitTargeting);
        Assert.False(packages[0].RequiresExplicitTargeting);
    }

    [Fact]
    public void MisalignedRowFallsBackToReadingFromTheRight()
    {
        const string output =
            "Name      Id         Version Available Source\n" +
            "---------------------------------------------\n" +
            "Overlong-name-without-spaces Vendor.App 1.0 2.0 winget\n";
        var p = Assert.Single(WingetUpgradeParser.Parse(output));
        Assert.Equal(("Overlong-name-without-spaces", "Vendor.App", "1.0", "2.0"), (p.Name, p.Id, p.Version, p.Available));
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.\r\n")]
    [InlineData("Nenhum pacote instalado encontrado que corresponda aos critérios de entrada.")]
    [InlineData("Name Id Version Available Source\n")]
    public void EmptyOrMessageOnlyOutputHasNoPackages(string output) => Assert.Empty(WingetUpgradeParser.Parse(output));

    [Fact]
    public void FooterSentencesAreNotPackages()
    {
        const string output =
            "Name      Id         Version Available Source\n" +
            "---------------------------------------------\n" +
            "App       Vendor.App 1.0     2.0       winget\n" +
            "2 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\n";
        Assert.Equal(["Vendor.App"], WingetUpgradeParser.Parse(output).Select(p => p.Id));
    }

    [Fact]
    public void AnsiEscapesAndProgressBarsAreDropped()
    {
        const string output =
            "\u001b[?25l  ██████████████▒▒▒▒▒▒  1.00 MB / 2.00 MB\r\n\u001b[0m" +
            "Name      Id         Version Available Source\r\n" +
            "---------------------------------------------\r\n" +
            "\u001b[32mApp\u001b[0m       Vendor.App 1.0     2.0       winget\r\n";
        Assert.Equal(["Vendor.App"], WingetUpgradeParser.Parse(output).Select(p => p.Id));
    }
}

public class WingetSourceTests
{
    [Fact]
    public async Task ListsWithAgreementsAcceptedAndWithoutPrompts()
    {
        var runner = new FakeToolRunner { Respond = _ => new ToolResult(0, "Name Id  Version Available Source\n----------------------------------\nApp  A.B 1.0     2.0       msstore\n", "") };
        var outcomes = await new WingetSource(runner, @"C:\winget.exe").UpdatesAsync();
        Assert.Equal(["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"], runner.Calls.Single().Args);
        var update = Assert.IsType<UpdateCheckOutcome.Available>(Assert.Single(outcomes)).Update;
        Assert.Equal(UpdateSourceKind.MicrosoftStore, update.Source);
        Assert.False(update.CanInstallWithWinget);
    }

    [Fact]
    public async Task MissingWingetOrTimeoutIsAFailureNotAnException()
    {
        var missing = await new WingetSource(new FakeToolRunner { Missing = true }, "winget.exe").UpdatesAsync();
        Assert.IsType<UpdateCheckOutcome.Failed>(Assert.Single(missing));
        var slow = await new WingetSource(new FakeToolRunner { Respond = _ => new ToolResult(-1, "", "", TimedOut: true) }, "winget.exe").UpdatesAsync();
        Assert.Contains("3 minutos", Assert.IsType<UpdateCheckOutcome.Failed>(Assert.Single(slow)).Failure.Message);
    }

    [Fact]
    public void TwoRowsWithOneIdAreTwoCopies()
    {
        var updates = WingetSource.ToUpdates([new("Zed", "Z.Z", "1", "2", "winget"), new("Zed", "Z.Z", "1", "2", "winget")]);
        Assert.Equal([0, 1], updates.Select(u => u.Copy));
        Assert.NotEqual(updates[0].Id, updates[1].Id);
    }
}

public class WingetInstallerTests
{
    private static AvailableUpdate Update(string id, UpdateSourceKind source = UpdateSourceKind.Winget, string current = "1.0") => new()
    {
        AppId = id, Name = id, Source = source, CurrentVersion = current, NewVersion = "2.0", SourceName = "winget",
    };

    [Fact]
    public async Task InstallsOnePackageByExactIdSilently()
    {
        var runner = new FakeToolRunner();
        var result = await new WingetInstaller(runner, @"C:\winget.exe").InstallAsync(Update("Microsoft.WindowsTerminal"));
        Assert.True(result.Succeeded);
        var (exe, args) = runner.Calls.Single();
        Assert.Equal(@"C:\winget.exe", exe);
        Assert.Equal(["upgrade", "--id", "Microsoft.WindowsTerminal", "--exact", "--source", "winget", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], args);
    }

    [Fact]
    public async Task UnknownInstalledVersionAddsIncludeUnknown()
    {
        var runner = new FakeToolRunner();
        await new WingetInstaller(runner, "winget.exe").InstallAsync(Update("EPSON.EpsonScan2", current: "Unknown"));
        Assert.Contains("--include-unknown", runner.Calls.Single().Args);
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("-h")]
    [InlineData("A B")]
    [InlineData("Vendor.Truncated…")]
    [InlineData("")]
    public async Task RefusesIdsThatCouldBeReadAsOptionsOrAreIncomplete(string id)
    {
        var runner = new FakeToolRunner();
        var update = Update(id) with { IsIdTruncated = id.EndsWith('…') };
        var result = await new WingetInstaller(runner, "winget.exe").InstallAsync(update);
        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task StoreUpdatesAreNeverInstalledByWinget()
    {
        var runner = new FakeToolRunner();
        var result = await new WingetInstaller(runner, "winget.exe").InstallAsync(Update("9NCBCSZSJRSB", UpdateSourceKind.MicrosoftStore));
        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InstallsNeverOverlap()
    {
        var runner = new FakeToolRunner { Delay = TimeSpan.FromMilliseconds(40) };
        var installer = new WingetInstaller(runner, "winget.exe");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => installer.InstallAsync(Update($"Vendor.App{i}"))));
        Assert.Equal(4, runner.Calls.Count);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task FailuresAreDescribed()
    {
        var hash = new FakeToolRunner { Respond = _ => new ToolResult(unchecked((int)0x8A150011), "", "") };
        Assert.Contains("hash", (await new WingetInstaller(hash, "w").InstallAsync(Update("A.B"))).Error);
        var other = new FakeToolRunner { Respond = _ => new ToolResult(5, "Found App [A.B]\nInstaller failed with exit code: 1603\n", "") };
        var error = (await new WingetInstaller(other, "w").InstallAsync(Update("A.B"))).Error!;
        Assert.Contains("0x00000005", error);
        Assert.Contains("1603", error);
        var slow = new FakeToolRunner { Respond = _ => new ToolResult(-1, "", "", TimedOut: true) };
        Assert.Contains("30 minutos", (await new WingetInstaller(slow, "w").InstallAsync(Update("A.B"))).Error);
    }
}
