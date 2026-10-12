using Cleanup.Core.Safety;

namespace Cleanup.Core.Tests.Safety;

public class SafetyPolicyTests : IDisposable
{
    private readonly TempHome home = new();
    public void Dispose() => home.Dispose();

    private bool Allowed(string path) => home.Policy().Verdict(path).IsAllowed;
    private string H(string relative) => Path.Combine(home.Home, relative);

    [Fact]
    public void AllowsChildrenOfCacheFolders()
    {
        Assert.True(Allowed(H(@"AppData\Local\Temp\abc.tmp")));
        Assert.True(Allowed(H(@"AppData\Local\CrashDumps\app.dmp")));
        Assert.True(Allowed(H(@"Downloads\setup.exe")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AppData")]
    [InlineData(@"AppData\Local")]
    [InlineData(@"AppData\Roaming")]
    [InlineData(@"AppData\Local\Temp")]
    [InlineData("Documents")]
    [InlineData("Downloads")]
    [InlineData("Desktop")]
    public void ContainersThemselvesAreDenied(string relative) => Assert.False(Allowed(relative.Length == 0 ? home.Home : H(relative)));

    [Theory]
    [InlineData(@".ssh\id_ed25519")]
    [InlineData(@"AppData\Roaming\Microsoft\Credentials\x")]
    [InlineData(@"AppData\Roaming\Microsoft\Protect\S-1\key")]
    [InlineData(@"AppData\Local\Microsoft\Vault\x")]
    public void SecretsAreDenied(string relative) => Assert.False(Allowed(H(relative)));

    [Fact]
    public void AncestorsOfHomeAndOtherUsersAreDenied()
    {
        Assert.False(Allowed(Path.GetDirectoryName(home.Home)!));
        Assert.False(Allowed(home.Root));
        Assert.False(Allowed(Path.Combine(home.Root, "Users", "someone", "file.txt")));
    }

    [Fact]
    public void WindowsFolderOnlyAllowsTheCleanableRoots()
    {
        Assert.False(Allowed(Path.Combine(home.Windows, @"System32\kernel32.dll")));
        Assert.False(Allowed(Path.Combine(home.Windows, "Temp")));
        Assert.True(Allowed(Path.Combine(home.Windows, @"Temp\old.tmp")));
        Assert.True(Allowed(Path.Combine(home.Windows, @"SoftwareDistribution\Download\abc")));
        Assert.False(Allowed(Path.Combine(home.Windows, @"SoftwareDistribution\DataStore")));
    }

    [Fact]
    public void ProgramFilesAndProgramDataAreDenied()
    {
        Assert.False(Allowed(Path.Combine(home.Root, @"Program Files\App\app.exe")));
        Assert.False(Allowed(Path.Combine(home.ProgramData, @"Vendor\data")));
        Assert.True(Allowed(Path.Combine(home.ProgramData, @"Microsoft\Windows\WER\ReportArchive\r1")));
    }

    [Fact]
    public void DriveRootsAndSystemFoldersAreDenied()
    {
        Assert.False(Allowed(@"C:\"));
        Assert.False(Allowed(@"C:\$Recycle.Bin\S-1-5\$R123"));
        Assert.False(Allowed(@"C:\System Volume Information\x"));
        Assert.False(Allowed(@"C:\pagefile.sys"));
    }

    [Fact]
    public void RelativeAndNetworkPathsAreDenied()
    {
        Assert.False(Allowed(@"Temp\x"));
        Assert.False(Allowed(@"\\server\share\x"));
    }

    [Fact]
    public void IgnoreListWins()
    {
        var cache = H(@"AppData\Local\Temp\keep");
        Assert.False(home.Policy(cache).Verdict(cache + @"\inner.txt").IsAllowed);
        Assert.True(home.Policy(cache).Verdict(H(@"AppData\Local\Temp\other")).IsAllowed);
    }

    [Fact]
    public void ComparisonIgnoresCaseAndSeparators()
    {
        Assert.False(Allowed(H(@"APPDATA\LOCAL").ToUpperInvariant()));
        Assert.False(Allowed(H("Documents").Replace('\\', '/')));
    }
}
