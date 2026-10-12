using Cleanup.Core.Uninstaller;

namespace Cleanup.Core.Tests.Uninstaller;

/// <summary>Mirrors the Swift "Uninstaller" / "App names" suites for registry Uninstall keys.</summary>
public class AppInventoryTests
{
    private const string Key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static InstalledApp? Load(TempHome t, string keyName, params (string Name, object? Value)[] values) =>
        AppInventory.FromUninstallKey(Key + keyName, "HKCU", 64, values.ToDictionary(v => v.Name, v => v.Value), t.Environment);

    [Fact]
    public void InventoryReadsNameVersionPublisherSizeAndFolders()
    {
        using var t = new TempHome();
        var folder = Path.Combine(t.Root, "Program Files", "Arduino IDE");
        var app = Load(t, "{ABC}", ("DisplayName", "Arduino IDE 2.3.10"), ("DisplayVersion", "2.3.10"), ("Publisher", "Arduino SA"),
            ("EstimatedSize", 540194), ("DisplayIcon", $"\"{folder}\\Arduino IDE.exe\",0"), ("UninstallString", $"\"{folder}\\Uninstall Arduino IDE.exe\" /allusers"))!;
        Assert.Equal("Arduino IDE 2.3.10", app.Name);
        Assert.Equal("2.3.10", app.Version);
        Assert.Equal("Arduino SA", app.Publisher);
        Assert.Equal(540194L * 1024, app.Size);
        Assert.Equal(folder, app.InstallFolder); // from the icon when InstallLocation is missing
        Assert.Equal(folder + @"\Arduino IDE.exe", app.MainExecutable);
        Assert.Equal("win32:hkcu:{abc}", app.Id);
        Assert.False(app.IsSystemApp);
    }

    [Fact]
    public void SkipsSystemComponentsUpdatesAndNamelessEntries()
    {
        using var t = new TempHome();
        Assert.Null(Load(t, "a", ("DisplayName", "Driver"), ("SystemComponent", 1), ("UninstallString", "x.exe")));
        Assert.Null(Load(t, "b", ("UninstallString", "x.exe")));
        Assert.Null(Load(t, "c", ("DisplayName", "  "), ("UninstallString", "x.exe")));
        Assert.Null(Load(t, "d", ("DisplayName", "Patch"), ("ParentKeyName", "Office"), ("UninstallString", "x.exe")));
        Assert.Null(Load(t, "e", ("DisplayName", "Fix"), ("ReleaseType", "Security Update"), ("UninstallString", "x.exe")));
        Assert.Null(Load(t, "f", ("DisplayName", "Atualização de Segurança (KB5034441)"), ("UninstallString", "x.exe")));
        Assert.NotNull(Load(t, "g", ("DisplayName", "Real App"), ("SystemComponent", 0), ("UninstallString", @"C:\x.exe")));
    }

    [Fact]
    public void SystemAppsAreFlagged()
    {
        using var t = new TempHome();
        Assert.True(Load(t, "a", ("DisplayName", "Sem desinstalador"))!.IsSystemApp);
        Assert.True(Load(t, "b", ("DisplayName", "Fixo"), ("NoRemove", 1), ("UninstallString", @"C:\u.exe"))!.IsSystemApp);
        Assert.True(Load(t, "c", ("DisplayName", "Componente"), ("Publisher", "Microsoft Windows"), ("UninstallString", @"C:\u.exe"))!.IsSystemApp);
        Assert.False(Load(t, "d", ("DisplayName", "Office"), ("Publisher", "Microsoft Corporation"), ("UninstallString", @"C:\u.exe"))!.IsSystemApp);
    }

    [Fact]
    public void MergeDropsRepeatsAcrossViewsAndSortsByName()
    {
        var a = Apps.Win32("Zeta", id: "1") with { Version = "1" };
        var b = Apps.Win32("álbum", id: "2");
        var c = Apps.Win32("Zeta", id: "3") with { Version = "1" };
        Assert.Equal(["1", "2"], AppInventory.Merge([a, b, c]).Select(x => x.Id).Order());
        Assert.Equal(["álbum", "Zeta"], AppInventory.Merge([a, b, c]).Select(x => x.Name));
    }

    [Fact]
    public void IconsBecomeExecutablesOnlyWhenTheyAreOne()
    {
        Assert.Equal(@"C:\A B\app.exe", AppInventory.ExecutableFromIcon("\"C:\\A B\\app.exe\",0"));
        Assert.Equal(@"C:\A B\app.exe", AppInventory.ExecutableFromIcon(@"C:\A B\app.exe,-101"));
        Assert.Equal(@"C:\A\app.exe", AppInventory.ExecutableFromIcon(@"C:\A\app.exe"));
        Assert.Null(AppInventory.ExecutableFromIcon(@"C:\A\app.ico"));
        Assert.Null(AppInventory.ExecutableFromIcon("app.exe"));
        Assert.Null(AppInventory.ExecutableFromIcon(null));
    }

    [Fact]
    public void SharedFoldersAreNeverAnAppsInstallFolder()
    {
        using var t = new TempHome();
        var env = t.Environment;
        Assert.Null(AppInventory.SpecificFolder(Path.Combine(t.Root, "Program Files"), env));
        Assert.Null(AppInventory.SpecificFolder(@"C:\", env));
        Assert.Null(AppInventory.SpecificFolder(@"C:\Tools", env));
        Assert.Null(AppInventory.SpecificFolder(Path.Combine(t.Windows, "System32", "spool"), env));
        Assert.Null(AppInventory.SpecificFolder(Path.Combine(t.ProgramData, "Package Cache", "{X}"), env));
        Assert.Null(AppInventory.SpecificFolder(Path.Combine(t.Home, @"AppData\Local\Programs"), env));
        Assert.Null(AppInventory.SpecificFolder(@"relative\path", env));
        Assert.Equal(Path.Combine(t.Root, "Program Files", "Git"), AppInventory.SpecificFolder(Path.Combine(t.Root, "Program Files", "Git") + "\\", env));
    }

    [Fact]
    public void CleansProductNames()
    {
        Assert.Equal("Mozilla Firefox", AppNames.CleanProductName("Mozilla Firefox (x64 pt-BR)"));
        Assert.Equal("7-Zip", AppNames.CleanProductName("7-Zip 23.01 (x64)"));
        Assert.Equal("Notepad++", AppNames.CleanProductName("Notepad++ (64-bit x64)"));
        Assert.Equal("Foo", AppNames.CleanProductName("Foo v2.1 64-bit"));
        Assert.Equal("Arduino IDE", AppNames.CleanProductName("Arduino IDE 2.3.10"));
        Assert.Equal("Docker Desktop", AppNames.CleanProductName("Docker Desktop"));
        Assert.Equal("2048", AppNames.CleanProductName("2048"));
    }

    [Fact]
    public void VendorsDropCompanySuffixes()
    {
        Assert.Equal("Docker", AppNames.Vendor("Docker Inc."));
        Assert.Equal("Mozilla", AppNames.Vendor("Mozilla Corporation"));
        Assert.Equal("Google", AppNames.Vendor("Google LLC"));
        Assert.Equal("Valve", AppNames.Vendor("Valve Corporation"));
        Assert.Equal("Seiko Epson", AppNames.Vendor("Seiko Epson Corporation"));
        Assert.Equal("Actions Semiconductor", AppNames.Vendor("Actions Semiconductor Co., LTD"));
        Assert.Equal("Spotify", AppNames.Vendor("CN=Spotify AB, O=Spotify AB, C=SE"));
        Assert.Null(AppNames.Vendor(" "));
    }

    [Fact]
    public void ProductNamesSkipShortAndGenericNames()
    {
        var chrome = Apps.Win32("Google Chrome", "Google LLC", folder: @"C:\Program Files\Google\Chrome\Application",
            exe: @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        // "Application" is generic; "chrome" comes from the executable and from the name without the vendor.
        Assert.Equal(["Google Chrome", "Chrome"], AppNames.ProductNames(chrome));
        Assert.Empty(AppNames.ProductNames(Apps.Win32("Git", exe: @"C:\Git\git.exe")));
        Assert.Equal(["Google", "Chrome"], AppNames.VendorNames(chrome));
    }
}

public class UninstallCommandTests
{
    private const string Windows = @"C:\Windows";

    [Fact]
    public void QuotedExecutableKeepsTheRestAsArguments()
    {
        var c = UninstallCommand.Parse("\"C:\\Program Files\\Git\\unins000.exe\" /SILENT", Windows)!;
        Assert.Equal(@"C:\Program Files\Git\unins000.exe", c.FileName);
        Assert.Equal("/SILENT", c.Arguments);
    }

    [Fact]
    public void UnquotedPathWithSpacesStopsAtTheFirstExe()
    {
        var c = UninstallCommand.Parse(@"C:\Program Files\Foo Bar\uninst.exe /S /D=C:\x", Windows)!;
        Assert.Equal(@"C:\Program Files\Foo Bar\uninst.exe", c.FileName);
        Assert.Equal(@"/S /D=C:\x", c.Arguments);
        Assert.Equal(@"C:\PROGRA~1\DIFX\B60D12~1\dpinst64.exe", UninstallCommand.Parse(@"C:\PROGRA~1\DIFX\B60D12~1\dpinst64.exe /u C:\a.inf", Windows)!.FileName);
    }

    [Fact]
    public void MsiProductsAreRemovedNotModified()
    {
        var c = UninstallCommand.Parse("MsiExec.exe /I{23170F69-40C1-2702-2301-000001000000}", Windows)!;
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", c.FileName);
        Assert.Equal("/X{23170F69-40C1-2702-2301-000001000000}", c.Arguments);
        Assert.Equal("/X{23170F69-40C1-2702-2301-000001000000}", UninstallCommand.Parse("msiexec /x {23170F69-40C1-2702-2301-000001000000}".Replace("msiexec ", "msiexec.exe "), Windows)!.Arguments);
        Assert.Null(UninstallCommand.Parse("MsiExec.exe /I{not-a-guid} & calc.exe", Windows));
        Assert.Null(UninstallCommand.Parse("MsiExec.exe /I{23170F69-40C1-2702-2301-000001000000}&calc.exe", Windows));
    }

    [Fact]
    public void ShellsAndOddCommandsAreRefused()
    {
        Assert.Null(UninstallCommand.Parse(@"cmd.exe /c del C:\*", Windows));
        Assert.Null(UninstallCommand.Parse(@"C:\Windows\System32\cmd.exe /c rd /s /q C:\", Windows));
        Assert.Null(UninstallCommand.Parse("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -c x", Windows));
        Assert.Null(UninstallCommand.Parse(@"uninst.exe /S", Windows));            // relative: whatever is in the current folder
        Assert.Null(UninstallCommand.Parse(@"C:\App\uninstall.bat", Windows));     // not an executable
        Assert.Null(UninstallCommand.Parse("\"C:\\App\\un.exe", Windows));          // unterminated quote
        Assert.Null(UninstallCommand.Parse(@"%ProgramFiles%\App\un.exe", Windows)); // unexpanded variable
        Assert.Null(UninstallCommand.Parse(null, Windows));
    }

    /// <summary>Shell metacharacters stay plain arguments for the vendor's program: no shell ever sees them.</summary>
    [Fact]
    public void MetacharactersStayArguments()
    {
        var c = UninstallCommand.Parse("\"C:\\App\\un.exe\" /S & calc.exe | x > y", Windows)!;
        Assert.Equal(@"C:\App\un.exe", c.FileName);
        Assert.Equal("/S & calc.exe | x > y", c.Arguments);
    }
}
