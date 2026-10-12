using System.Diagnostics;
using Cleanup.Core.Admin;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Privacy;

namespace Cleanup.Core.Tests.Privacy;

public sealed class PrivacyTests : IDisposable
{
    private readonly TempHome t = new();
    public void Dispose() => t.Dispose();

    private const string Chrome = @"AppData\Local\Google\Chrome\User Data";
    private const string Firefox = @"AppData\Roaming\Mozilla\Firefox\Profiles";
    private const string Recent = @"AppData\Roaming\Microsoft\Windows\Recent";
    private const string PSReadLine = @"AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine";

    private void MakeChrome()
    {
        foreach (var profile in (string[])["Default", "Profile 1"])
        {
            foreach (var name in (string[])["History", "History-journal", "Visited Links", "Top Sites", "Shortcuts", "Cookies",
                                             "Login Data", "Bookmarks", "Preferences", "Web Data", "Secure Preferences"])
                t.File($@"{Chrome}\{profile}\{name}", 1000);
        }
        t.File($@"{Chrome}\Default\Network\Cookies", 500);
        t.File($@"{Chrome}\Local State", 100);
    }

    private PrivacyScanResult Scan(params string[] running) => new PrivacyScanner(t.Environment, running.ToHashSet()).Scan();

    private static string Name(string path) => Path.GetFileName(path);

    [Fact]
    public void FindsChromiumHistoryAndCookiesPerProfile()
    {
        MakeChrome();
        var items = Scan().Items.Where(i => i.App == "Chrome").ToList();
        Assert.Equal(["chrome.Default.cookies", "chrome.Default.history", "chrome.Profile 1.cookies", "chrome.Profile 1.history"],
            items.Select(i => i.Id).Order(StringComparer.Ordinal));
        var history = items.Single(i => i.Id == "chrome.Default.history");
        Assert.Equal(["History", "History-journal", "Shortcuts", "Top Sites", "Visited Links"], history.Paths.Select(Name).Order(StringComparer.Ordinal));
        Assert.Equal(PrivacyKind.History, history.Kind);
        Assert.Equal("Default", history.Profile);
        var cookies = items.Single(i => i.Id == "chrome.Default.cookies");
        Assert.Equal(2, cookies.Paths.Count);
        Assert.Contains(cookies.Paths, p => p.EndsWith(@"Default\Network\Cookies", StringComparison.Ordinal));
        Assert.Equal(1500, cookies.Size);
        Assert.Contains("sair dos sites", cookies.Loses);
        Assert.All(items, i => Assert.False(i.IsBlocked));
    }

    [Fact]
    public void FindsEdgeBraveVivaldiAndOperaInTheirOwnFolders()
    {
        t.File(@"AppData\Local\Microsoft\Edge\User Data\Default\History");
        t.File(@"AppData\Local\BraveSoftware\Brave-Browser\User Data\Profile 2\Network\Cookies");
        t.File(@"AppData\Local\Vivaldi\User Data\Default\Visited Links");
        t.File(@"AppData\Roaming\Opera Software\Opera Stable\History"); // Opera's single profile lives in the root
        t.File(@"AppData\Roaming\Opera Software\Opera Stable\Login Data");
        var ids = Scan().Items.Select(i => i.Id).ToHashSet();
        Assert.Equal(["brave.Profile 2.cookies", "edge.Default.history", "opera.Default.history", "vivaldi.Default.history"],
            ids.Order(StringComparer.Ordinal));
        var opera = Scan().Items.Single(i => i.App == "Opera");
        Assert.Equal(["History"], opera.Paths.Select(Name));
    }

    /// <summary>Passwords, bookmarks, settings and Firefox's history-with-bookmarks file are never offered.</summary>
    [Fact]
    public void ProtectedFilesAreNeverListed()
    {
        MakeChrome();
        foreach (var name in (string[])["places.sqlite", "logins.json", "key4.db", "cookies.sqlite", "formhistory.sqlite", "prefs.js"])
            t.File($@"{Firefox}\abc.default\{name}");
        var names = Scan().Items.SelectMany(i => i.Paths).Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var forbidden in (string[])["Login Data", "Bookmarks", "Preferences", "Secure Preferences", "Web Data", "Local State",
                                             "places.sqlite", "logins.json", "key4.db", "prefs.js"])
            Assert.DoesNotContain(forbidden, names);
        Assert.Contains("cookies.sqlite", names);
        Assert.Contains("formhistory.sqlite", names);
    }

    [Fact]
    public void RunningBrowserItemsAreBlocked()
    {
        MakeChrome();
        var items = Scan("chrome").Items.Where(i => i.App == "Chrome").ToList();
        Assert.All(items, i => Assert.True(i.IsBlocked));
        Assert.Equal("Feche o Chrome", items[0].BlockedReason);
        // Another browser running doesn't block Chrome; process names compare case-insensitively.
        Assert.All(Scan("msedge").Items, i => Assert.False(i.IsBlocked));
        Assert.All(Scan("Chrome").Items, i => Assert.True(i.IsBlocked));
    }

    [Fact]
    public void FirefoxRecentItemsAndShellHistory()
    {
        t.File($@"{Firefox}\x.default-release\formhistory.sqlite", 300);
        t.File($@"{Firefox}\x.default-release\cookies.sqlite", 300);
        t.File($@"{Firefox}\x.default-release\cookies.sqlite-wal", 30);
        t.File($@"{Recent}\report.docx.lnk");
        t.File($@"{Recent}\AutomaticDestinations\5f7b5f1e01b83767.automaticDestinations-ms");
        t.File($@"{Recent}\CustomDestinations\28c8b86deab549a1.customDestinations-ms");
        t.File($@"{PSReadLine}\ConsoleHost_history.txt", 900);
        var items = Scan().Items;
        Assert.Equal(["firefox.x.default-release.cookies", "firefox.x.default-release.forms", "powershell.shellHistory", "windows.recentItems"],
            items.Select(i => i.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, items.Single(i => i.Id == "firefox.x.default-release.cookies").Paths.Count);
        Assert.Equal(3, items.Single(i => i.Id == "windows.recentItems").Paths.Count);
        var shell = items.Single(i => i.Id == "powershell.shellHistory");
        Assert.Equal(["ConsoleHost_history.txt"], shell.Paths.Select(Name));
        Assert.Equal(PrivacyKind.ShellHistory, shell.Kind);
    }

    /// <summary>The Quick Access jump list holds the folders the user pinned in Explorer: never touched.</summary>
    [Fact]
    public void RecentItemsNeverIncludeQuickAccessPins()
    {
        t.File($@"{Recent}\AutomaticDestinations\f01b4d95cf55d32a.automaticDestinations-ms");
        t.File($@"{Recent}\AutomaticDestinations\F01B4D95CF55D32A.AUTOMATICDESTINATIONS-MS.bak");
        t.File($@"{Recent}\AutomaticDestinations\9b9cdc69c1c24e2b.automaticDestinations-ms");
        t.File($@"{Recent}\desktop.ini");
        t.File($@"{Recent}\notes.txt");
        t.File($@"{Recent}\sub\deep.lnk");
        var recent = Scan().Items.Single(i => i.Id == "windows.recentItems");
        Assert.Equal(["9b9cdc69c1c24e2b.automaticDestinations-ms"], recent.Paths.Select(Name));
    }

    [Fact]
    public void OnlyQuickAccessPinsMeansNoRecentItem()
    {
        t.File($@"{Recent}\AutomaticDestinations\f01b4d95cf55d32a.automaticDestinations-ms");
        Assert.Empty(Scan().Items);
    }

    /// <summary>Links are skipped: deleting a link leaves the data (a lie), and a linked folder would be emptied for real.</summary>
    [Fact]
    public void JunctionedFoldersAreNeverWalked()
    {
        var elsewhere = t.Dir(@"Documents\notes");
        t.File(@"Documents\notes\History");
        t.File(@"Documents\notes\ConsoleHost_history.txt");
        t.File(@"Documents\notes\cookies.sqlite");
        t.File(@"Documents\notes\x.lnk");
        t.Dir(@"AppData\Local\Google\Chrome\User Data");
        t.Dir(@"AppData\Roaming\Microsoft\Windows\PowerShell");
        t.Dir(@"AppData\Roaming\Mozilla\Firefox\Profiles");
        t.Dir(@"AppData\Roaming\Microsoft\Windows");
        Junction(Path.Combine(t.Home, Chrome, "Default"), elsewhere);       // a linked profile
        Junction(Path.Combine(t.Home, PSReadLine), elsewhere);              // a linked PSReadLine folder
        Junction(Path.Combine(t.Home, Firefox, "abc.default"), elsewhere);  // a linked Firefox profile
        Junction(Path.Combine(t.Home, Recent), elsewhere);                  // a linked Recent folder
        Assert.Empty(Scan().Items);
    }

    [Fact]
    public void AJunctionedNetworkFolderKeepsOnlyTheRealCookies()
    {
        var elsewhere = t.Dir(@"Documents\net");
        t.File(@"Documents\net\Cookies");
        t.File($@"{Chrome}\Default\Cookies");
        Junction(Path.Combine(t.Home, Chrome, @"Default\Network"), elsewhere);
        var cookies = Scan().Items.Single(i => i.Id == "chrome.Default.cookies");
        Assert.Equal([Path.Combine(t.Home, Chrome, @"Default\Cookies")], cookies.Paths);
    }

    [Fact]
    public void SymlinkedFilesAreNeverOffered()
    {
        var real = t.File(@"dotfiles\history.txt", 500);
        var link = Path.Combine(t.Dir(PSReadLine), "ConsoleHost_history.txt");
        try { File.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // needs Developer Mode or admin
        Assert.Empty(Scan().Items);
    }

    /// <summary>Open shells write their history back when they exit.</summary>
    [Fact]
    public void ShellHistoryIsBlockedWhileATerminalIsOpen()
    {
        t.File($@"{PSReadLine}\ConsoleHost_history.txt");
        foreach (var terminal in (string[])["windowsterminal", "powershell", "pwsh", "cmd"])
        {
            var shell = Scan(terminal).Items.Single(i => i.Id == "powershell.shellHistory");
            Assert.True(shell.IsBlocked, terminal);
            Assert.Equal("Feche o PowerShell, o cmd e o Terminal", shell.BlockedReason);
        }
        Assert.False(Scan("notepad").Items.Single().IsBlocked);
    }

    [Fact]
    public void FirefoxItemsAreBlockedWhileFirefoxRuns()
    {
        t.File($@"{Firefox}\a.dev-edition-default\cookies.sqlite");
        Assert.All(Scan("firefox").Items, i => Assert.True(i.IsBlocked));
    }

    [Fact]
    public void RecentItemsAreNeverBlocked()
    {
        t.File($@"{Recent}\a.lnk");
        Assert.False(Scan("explorer", "chrome", "firefox", "powershell").Items.Single().IsBlocked);
    }

    [Fact]
    public void MissingFilesMeanNoItem()
    {
        t.Dir($@"{Chrome}\Default");
        t.Dir($@"{Firefox}\x.default");
        t.Dir(PSReadLine);
        Assert.Empty(Scan().Items);
    }

    [Fact]
    public void CleanerDeletesExactlyTheItemFilesPermanently()
    {
        MakeChrome();
        var history = Scan().Items.Single(i => i.Id == "chrome.Default.history");
        var remover = new FakeRemover();
        var result = PrivacyCleaner.Clean([history], new HashSet<string>(), t.Policy(), remover);
        Assert.Equal(["chrome.Default.history"], result.Cleaned.Select(i => i.Id));
        Assert.Equal(history.Paths.Order(), remover.Deleted.Order());
        Assert.Empty(remover.Recycled);
        Assert.Equal(history.Size, result.FreedBytes);
    }

    [Fact]
    public void CleanerReallyDeletesWithTheFileSystem()
    {
        t.File($@"{PSReadLine}\ConsoleHost_history.txt", 50);
        var keep = t.File($@"{PSReadLine}\Other_history.txt", 50);
        var shell = Scan().Items.Single();
        var result = PrivacyCleaner.Clean([shell], new HashSet<string>(), t.Policy(), new DeletingRemover());
        Assert.Single(result.Cleaned);
        Assert.False(File.Exists(shell.Paths[0]));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void CleanerRefusesARunningBrowserAtDeleteTime()
    {
        MakeChrome();
        var cookies = Scan().Items.Single(i => i.Id == "chrome.Default.cookies");
        var remover = new FakeRemover();
        var result = PrivacyCleaner.Clean([cookies], new HashSet<string> { "CHROME" }, t.Policy(), remover);
        Assert.Empty(result.Cleaned);
        Assert.Equal("Feche o Chrome", Assert.Single(result.Refused).Reason);
        Assert.Empty(remover.Deleted);
    }

    [Fact]
    public void CleanerHonoursTheSafetyPolicy()
    {
        MakeChrome();
        var history = Scan().Items.Single(i => i.Id == "chrome.Default.history");
        var remover = new FakeRemover();
        var result = PrivacyCleaner.Clean([history], new HashSet<string>(), t.Policy(Path.Combine(t.Home, Chrome)), remover);
        Assert.Empty(remover.Deleted);
        Assert.Empty(result.Cleaned);
        Assert.Equal(history.Paths.Count, result.Failed.Count);
        Assert.All(result.Failed, f => Assert.Contains("política de segurança", f.Reason));
    }

    /// <summary>A file swapped for a link between the scan and the delete is not removed.</summary>
    [Fact]
    public void CleanerRefusesAPathThatIsNoLongerARegularFile()
    {
        var file = t.File($@"{PSReadLine}\ConsoleHost_history.txt");
        var shell = Scan().Items.Single();
        File.Delete(file);
        Directory.CreateDirectory(file);
        var remover = new FakeRemover();
        var result = PrivacyCleaner.Clean([shell], new HashSet<string>(), t.Policy(), remover);
        Assert.Empty(remover.Deleted);
        Assert.Equal("não é um arquivo comum", Assert.Single(result.Failed).Reason);
    }

    [Fact]
    public void CleanerSkipsFilesThatAreAlreadyGone()
    {
        t.File($@"{Chrome}\Default\History");
        var journal = t.File($@"{Chrome}\Default\History-journal");
        var history = Scan().Items.Single();
        File.Delete(journal);
        var remover = new FakeRemover();
        var result = PrivacyCleaner.Clean([history], new HashSet<string>(), t.Policy(), remover);
        Assert.Single(result.Cleaned);
        Assert.Equal(["History"], remover.Deleted.Select(Name));
    }

    [Fact]
    public void ALockedFileMakesTheItemPartial()
    {
        t.File($@"{Chrome}\Default\History");
        t.File($@"{Chrome}\Default\Shortcuts");
        var history = Scan().Items.Single();
        var remover = new ThrowingRemover(Path.Combine(t.Home, Chrome, @"Default\Shortcuts"));
        var result = PrivacyCleaner.Clean([history], new HashSet<string>(), t.Policy(), remover);
        Assert.Empty(result.Cleaned);
        Assert.Equal("em uso por outro programa", Assert.Single(result.Failed).Reason);
        Assert.Equal(["History"], remover.Deleted.Select(Name));
    }

    internal static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["/c", "mklink", "/J", link, target]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction not created: " + link);
    }

    private sealed class DeletingRemover : IFileRemover
    {
        public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths) => throw new InvalidOperationException();
        public void Delete(string path) => File.Delete(path);
        public void EmptyRecycleBin(long expectedBytes, long expectedItems) => throw new InvalidOperationException();
    }

    private sealed class ThrowingRemover(string locked) : IFileRemover
    {
        public List<string> Deleted { get; } = [];
        public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths) => throw new InvalidOperationException();
        public void Delete(string path)
        {
            if (string.Equals(path, locked, StringComparison.OrdinalIgnoreCase)) throw new IOException("locked", unchecked((int)0x80070020));
            Deleted.Add(path);
        }
        public void EmptyRecycleBin(long expectedBytes, long expectedItems) => throw new InvalidOperationException();
    }
}

public sealed class WiFiNetworksTests
{
    private static readonly string ProfilesEnglish = """

        Profiles on interface Wi-Fi:

        Group policy profiles (read only)
        ---------------------------------
            <None>

        User profiles
        -------------
            All User Profile     : DeltaCorporativo
            All User Profile     : Café 5G{{TRAIL}}
            All User Profile     : a:b c
            Current User Profile : Casa

        """.Replace("{{TRAIL}}", " "); // an SSID with a trailing space keeps it

    private const string ProfilesPortuguese = "\r\nPerfis na interface Wi-Fi:\r\n\r\nPerfis de política de grupo (somente leitura)\r\n" +
        "---------------------------------\r\n    <Nenhum>\r\n\r\nPerfis de usuário\r\n-------------\r\n" +
        "    Todos os Perfis de Usuários: LA\r\n    Todos os Perfis de Usuários: DeltaCorporativo\r\n\r\n";

    private const string InterfacesConnected = """

        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6 AX201 160MHz
            GUID                   : 0a1b2c3d-0000-0000-0000-000000000000
            Physical address       : aa:bb:cc:dd:ee:ff
            Interface type         : Primary
            State                  : connected
            SSID                   : DeltaCorporativo
            AP BSSID               : 11:22:33:44:55:66
            Band                   : 5 GHz
            Channel                : 44
            Network type           : Infrastructure
            Radio type             : 802.11ax
            Authentication         : WPA2-Personal
            Cipher                 : CCMP
            Connection mode        : Auto Connect
            Signal                 : 92%
            Profile                : Delta (escritório)

            Hosted network status  : Not available

        """;

    private const string InterfacesDisconnected = """

        Há 1 interface no sistema:

            Nome                   : Wi-Fi
            Descrição              : Intel(R) Wi-Fi 6 AX201 160MHz
            Endereço físico        : aa:bb:cc:dd:ee:ff
            Estado                 : desconectado
            Status de rede hospedada  : Não disponível

        """;

    [Fact]
    public void ParsesSavedProfilesInAnyLanguage()
    {
        Assert.Equal(["DeltaCorporativo", "Café 5G ", "a:b c", "Casa"], WiFiNetworks.Profiles(ProfilesEnglish));
        Assert.Equal(["LA", "DeltaCorporativo"], WiFiNetworks.Profiles(ProfilesPortuguese));
        Assert.Empty(WiFiNetworks.Profiles("There is no wireless interface on the system."));
        Assert.Empty(WiFiNetworks.Profiles("Não há nenhuma interface sem fio no sistema."));
        Assert.Empty(WiFiNetworks.Profiles(""));
    }

    [Fact]
    public void ParsesTheNetworkInUse()
    {
        Assert.Equal(["Delta (escritório)", "DeltaCorporativo"], WiFiNetworks.Connected(InterfacesConnected).Order(StringComparer.Ordinal));
        Assert.Empty(WiFiNetworks.Connected(InterfacesDisconnected));
        Assert.Empty(WiFiNetworks.Connected(""));
        Assert.Equal(["Rede"], WiFiNetworks.Connected("    Estado : conectado\r\n    SSID : Rede\r\n    Perfil : Rede\r\n"));
    }

    [Fact]
    public void RemovalPlanSkipsTheNetworkInUseAndRefusedNames()
    {
        var connected = WiFiNetworks.Connected(InterfacesConnected);
        var plan = WiFiNetworks.Plan(["LA", "DeltaCorporativo", "Delta (escritório)", "bad\nname", "*", "Casa?", "x\" name=\"Casa", "trail\\", "LA"], connected);
        Assert.Equal(["LA"], plan.Names);
        Assert.Equal(["DeltaCorporativo", "Delta (escritório)", "bad\nname", "*", "Casa?", "x\" name=\"Casa", "trail\\"], plan.Skipped);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(Elevation.Ops.WifiDelete, step.Op);
        Assert.Equal("LA", step.Id);
        Assert.Null(step.Path);
    }

    /// <summary><c>netsh wlan delete profile name=*</c> deletes every saved network: wildcards are never passed.</summary>
    [Fact]
    public void WildcardsAreRefused()
    {
        Assert.NotNull(WiFiNetworks.Refusal("*"));
        Assert.NotNull(WiFiNetworks.Refusal("Home*"));
        Assert.NotNull(WiFiNetworks.Refusal("?"));
        Assert.NotNull(WiFiNetworks.Refusal(""));
        Assert.NotNull(WiFiNetworks.Refusal("   "));
        Assert.Null(WiFiNetworks.Refusal("it's $(x) `id` a b;c & del %PATH% Café"));
    }

    [Fact]
    public void DeleteArgumentsKeepTheNameInOneArgument()
    {
        Assert.Equal(["wlan", "delete", "profile", "name=a b;c & x"], WiFiNetworks.DeleteArguments("a b;c & x"));
    }

    [Fact]
    public void DecodesUtf8OrTheOemCodePage()
    {
        Assert.Equal("Não há", WiFiNetworks.Decode("Não há"u8.ToArray(), System.Text.Encoding.Latin1));
        Assert.Equal("Não", WiFiNetworks.Decode([0x4E, 0xE3, 0x6F], System.Text.Encoding.Latin1)); // not valid UTF-8 → fallback
    }
}
