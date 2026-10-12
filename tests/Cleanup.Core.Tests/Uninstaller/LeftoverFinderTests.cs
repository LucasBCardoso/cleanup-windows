using Cleanup.Core.Uninstaller;

namespace Cleanup.Core.Tests.Uninstaller;

/// <summary>Mirrors the Swift leftover tests (bundle id variants → product / Vendor\Product folders on Windows).</summary>
public class LeftoverFinderTests
{
    private static List<string> Paths(IEnumerable<Models.ScanItem> items) => items.Select(i => i.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void LeftoversMatchProductAndVendorProductFolders()
    {
        using var t = new TempHome();
        var app = Apps.Win32("Mozilla Firefox (x64 pt-BR)", "Mozilla Corporation", folder: Path.Combine(t.Root, "Program Files", "Mozilla Firefox"),
            exe: Path.Combine(t.Root, "Program Files", "Mozilla Firefox", "firefox.exe"));
        var a = t.File(@"AppData\Roaming\Mozilla\Firefox\profiles.ini", 100);
        var b = t.File(@"AppData\Local\Mozilla\Firefox\cache2\x", 200);
        var c = t.File(@"AppData\Local\Mozilla Firefox\x", 5);
        var d = t.File(@"AppData\LocalLow\Mozilla\Firefox\x", 5);
        t.File(@"AppData\Roaming\Mozilla\Thunderbird\x");      // another product of the same vendor
        t.File(@"AppData\Roaming\Firefoxy\x");                 // a prefix is not a match
        t.File(@"AppData\Roaming\Mozilla\x.txt");              // the vendor folder itself is never offered
        var found = new LeftoverFinder(t.Environment, t.Policy()).Find(app, [app]);
        Assert.Equal(Paths([Item(Path.GetDirectoryName(a)!), Item(Path.GetDirectoryName(Path.GetDirectoryName(b))!), Item(Path.GetDirectoryName(c)!),
                Item(Path.GetDirectoryName(d)!)]),
            Paths(found.Removable));
        Assert.All(found.Removable, i => Assert.Equal(LeftoverFinder.FileRule, i.RuleId));
        Assert.Equal(200, found.Removable.First(i => i.Path.EndsWith(@"Local\Mozilla\Firefox")).Size);
    }

    private static Models.ScanItem Item(string path) => Items.At(path);

    [Fact]
    public void ShortNamesDoNotMatchByName()
    {
        using var t = new TempHome();
        t.File(@"AppData\Roaming\Git\x");
        var app = Apps.Win32("Git", "The Git Development Community", exe: @"C:\Program Files\Git\git.exe");
        Assert.Empty(new LeftoverFinder(t.Environment, t.Policy()).Find(app, [app]).Removable);
    }

    [Fact]
    public void NamesSharedWithAnotherInstalledAppAreNotOffered()
    {
        using var t = new TempHome();
        t.File(@"AppData\Roaming\Python\x");
        t.File(@"AppData\Local\Python 3.12\x");
        var py311 = Apps.Win32("Python 3.11.4 (64-bit)", "Python Software Foundation", id: "a");
        var py312 = Apps.Win32("Python 3.12.1 (64-bit)", "Python Software Foundation", id: "b");
        var found = new LeftoverFinder(t.Environment, t.Policy()).Find(py311, [py311, py312]);
        Assert.Empty(found.Removable);
        // Alone, the same app gets its folder.
        Assert.Single(new LeftoverFinder(t.Environment, t.Policy()).Find(py311, [py311]).Removable);
    }

    [Fact]
    public void IgnoredLeftoversAreExcluded()
    {
        using var t = new TempHome();
        var kept = t.Dir(@"AppData\Roaming\Spotify");
        t.File(@"AppData\Local\Spotify\x");
        var app = Apps.Win32("Spotify", "Spotify AB");
        var found = new LeftoverFinder(t.Environment, t.Policy(kept)).Find(app, [app]);
        Assert.DoesNotContain(found.Removable, i => i.Path == kept);
        Assert.DoesNotContain(found.ReportOnly, i => i.Path == kept);
        Assert.Single(found.Removable);
    }

    [Fact]
    public void SystemFoldersAreReportOnly()
    {
        using var t = new TempHome();
        // "Docker" is a product name too (the install folder is Program Files\Docker\Docker), so the whole folder matches.
        t.Dir(Path.Combine(t.ProgramData, "Docker", "Docker Desktop"));
        var programData = Path.Combine(t.ProgramData, "Docker");
        var installed = t.Dir(Path.Combine(t.Root, "Program Files", "Docker", "Docker"));
        var commonMenu = t.File(Path.Combine(t.ProgramData, @"Microsoft\Windows\Start Menu\Programs\Docker Desktop.lnk"));
        var app = Apps.Win32("Docker Desktop", "Docker Inc.", folder: installed);
        var found = new LeftoverFinder(t.Environment, t.Policy()).Find(app, [app]);
        Assert.Empty(found.Removable);
        Assert.Equal(Paths([Item(programData), Item(installed), Item(commonMenu)]), Paths(found.ReportOnly));
        Assert.All(found.ReportOnly, i => Assert.Equal(LeftoverFinder.ReportRule, i.RuleId));
    }

    [Fact]
    public void PerUserInstallFolderAndStartMenuEntriesAreRemovable()
    {
        using var t = new TempHome();
        var folder = t.Dir(@"AppData\Local\Programs\Obsidian");
        t.File(@"AppData\Local\Programs\Obsidian\Obsidian.exe");
        var shortcut = t.File(@"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Obsidian.lnk");
        var menuFolder = t.Dir(@"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Obsidian Notes");
        t.File(@"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Outro.lnk");
        var app = Apps.Win32("Obsidian", "Obsidian", folder: folder, exe: Path.Combine(folder, "Obsidian.exe"));
        var found = new LeftoverFinder(t.Environment, t.Policy()).Find(app, [app]);
        Assert.Contains(found.Removable, i => i.Path == folder);
        Assert.Contains(found.Removable, i => i.Path == shortcut);
        Assert.DoesNotContain(found.Removable, i => i.Path == menuFolder);
    }

    [Fact]
    public void RegistryKeysUnderUserSoftwareAreOfferedButNeverSharedOnes()
    {
        using var t = new TempHome();
        var registry = new FakeRegistry(@"Mozilla\Firefox", @"Mozilla\Thunderbird", "Firefox", @"Microsoft\Firefox", "Classes");
        var app = Apps.Win32("Mozilla Firefox", "Mozilla Corporation");
        var found = new LeftoverFinder(t.Environment, t.Policy(), registry).Find(app, [app]);
        var keys = found.Removable.Where(i => i.RuleId == LeftoverFinder.RegistryRule).Select(i => i.Path).Order().ToList();
        Assert.Equal([@"HKEY_CURRENT_USER\Software\Firefox", @"HKEY_CURRENT_USER\Software\Mozilla\Firefox"], keys);
        Assert.Contains(found.Removable, i => i.DisplayName == @"Mozilla\Firefox");
    }

    [Fact]
    public void StoreAndSystemAppsHaveNoLeftovers()
    {
        using var t = new TempHome();
        t.File(@"AppData\Roaming\Calculadora\x");
        var finder = new LeftoverFinder(t.Environment, t.Policy());
        var store = Apps.Store("Calculadora", "Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        Assert.Same(Leftovers.None, finder.Find(store, [store]));
        var system = Apps.Win32("Calculadora", system: true);
        Assert.Same(Leftovers.None, finder.Find(system, [system]));
    }
}

public class RegistryPolicyTests
{
    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Mozilla\Firefox", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Spotify", true)]
    [InlineData(@"hkey_current_user\software\Vendor\Product\Sub", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Mozilla", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Office", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\Foo", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Policies\Foo", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\A\\B", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\A\..\Microsoft", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Foo\Bar", false)]
    [InlineData(@"HKEY_CURRENT_USER\Environment", false)]
    public void OnlyAnAppsOwnUserKeyMayBeDeleted(string key, bool allowed) => Assert.Equal(allowed, RegistryPolicy.IsAllowed(key));

    [Fact]
    public void ExportsInRegeditFormat()
    {
        var node = new RegNode(@"HKEY_CURRENT_USER\Software\V\P",
        [
            new RegValue("", RegKind.String, System.Text.Encoding.Unicode.GetBytes("padrão\0")),
            new RegValue("Path", RegKind.String, System.Text.Encoding.Unicode.GetBytes("C:\\a \"b\"\0")),
            new RegValue("Count", RegKind.DWord, BitConverter.GetBytes(255)),
            new RegValue("Big", RegKind.QWord, BitConverter.GetBytes(1L)),
            new RegValue("Blob", RegKind.Binary, [0xde, 0xad]),
            new RegValue("Exp", RegKind.ExpandString, System.Text.Encoding.Unicode.GetBytes("%A%\0")),
            new RegValue("Multi", RegKind.MultiString, System.Text.Encoding.Unicode.GetBytes("a\0\0")),
        ],
        [new RegNode(@"HKEY_CURRENT_USER\Software\V\P\Sub", [], [])]);
        var text = RegExport.Format(node);
        Assert.Equal(
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\V\\P]\r\n" +
            "@=\"padrão\"\r\n" +
            "\"Path\"=\"C:\\\\a \\\"b\\\"\"\r\n" +
            "\"Count\"=dword:000000ff\r\n" +
            "\"Big\"=hex(b):01,00,00,00,00,00,00,00\r\n" +
            "\"Blob\"=hex:de,ad\r\n" +
            "\"Exp\"=hex(2):25,00,41,00,25,00,00,00\r\n" +
            "\"Multi\"=hex(7):61,00,00,00,00,00\r\n" +
            "\r\n[HKEY_CURRENT_USER\\Software\\V\\P\\Sub]\r\n", text);
    }
}
