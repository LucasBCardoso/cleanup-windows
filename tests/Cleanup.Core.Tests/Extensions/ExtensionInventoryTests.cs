using Cleanup.Core.Cleaning;
using Cleanup.Core.Extensions;

namespace Cleanup.Core.Tests.Extensions;

internal sealed class ExtensionFixture : IDisposable
{
    public TempHome T { get; } = new();
    public string LocalAppData => Path.Combine(T.Home, @"AppData\Local");
    public string Roaming => Path.Combine(T.Home, @"AppData\Roaming");

    public static string Id(char c) => new(c, 32);

    /// <summary>A Chromium extension version folder with its manifest and locales.</summary>
    public string Chromium(string userData, string profile, string id, string version, string? manifest, Dictionary<string, string>? locales = null)
    {
        var folder = T.Dir(Path.Combine(LocalAppData, userData, profile, "Extensions", id, version));
        if (manifest is not null) File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest);
        foreach (var (locale, json) in locales ?? [])
        {
            var dir = T.Dir(Path.Combine(folder, "_locales", locale));
            File.WriteAllText(Path.Combine(dir, "messages.json"), json);
        }
        return Path.GetDirectoryName(folder)!;
    }

    /// <summary>Writes <c>extensions.settings</c> into a profile's Secure Preferences.</summary>
    public void Settings(string userData, string profile, string settingsJson, string file = "Secure Preferences") =>
        File.WriteAllText(Path.Combine(T.Dir(Path.Combine(LocalAppData, userData, profile)), file),
            "{\"extensions\":{\"settings\":" + settingsJson + "}}");

    public ExtensionInventory Inventory => new(T.Environment, measureSize: false);

    public void Dispose() => T.Dispose();
}

/// <summary>Mirrors the Swift "Extension inventory" suite, for Windows' browsers, screen savers and Office files.</summary>
public class ExtensionInventoryTests
{
    private const string Chrome = @"Google\Chrome\User Data";
    private const string Brave = @"BraveSoftware\Brave-Browser\User Data";

    [Fact]
    public void ListsChromiumExtensionsFromTheNewestVersionWithLocalizedNames()
    {
        using var f = new ExtensionFixture();
        var docs = ExtensionFixture.Id('g');
        f.Chromium(Chrome, "Default", docs, "1.9.0_0", """{"name":"Old","version":"1.9.0"}""");
        var folder = f.Chromium(Chrome, "Default", docs, "1.110.1_0", """{"name":"__MSG_extName__","version":"1.110.1","default_locale":"en_US"}""",
            new() { ["en_US"] = """{"EXTNAME":{"message":"Google Docs Offline"}}""" });
        f.Chromium(Brave, "Profile 1", ExtensionFixture.Id('a'), "8.9_0", """{"name":"GoFullPage","version":"8.9"}""");

        var items = f.Inventory.Load().Where(i => i.Kind == ExtensionKind.Browser).ToList();
        Assert.Equal(2, items.Count);
        var d = items.Single(i => i.Name == "Google Docs Offline");
        Assert.Equal("1.110.1", d.Version);
        Assert.Equal("Chrome", d.Browser);
        Assert.Equal("Default", d.Profile);
        Assert.Equal(folder, d.Path);
        Assert.False(d.IsRemovable); // no settings: origin unknown
        Assert.Contains(items, i => i.Name == "GoFullPage" && i.Browser == "Brave" && i.Profile == "Profile 1");
    }

    [Fact]
    public void MissingOrBrokenLocaleFallsBackToTheExtensionId()
    {
        using var f = new ExtensionFixture();
        f.Chromium(Chrome, "Default", ExtensionFixture.Id('b'), "3.6_0", """{"name":"__MSG_853__","version":"3.6","default_locale":"en"}""");
        f.Chromium(Chrome, "Default", ExtensionFixture.Id('c'), "1_0", """{"name":"__MSG_x__","version":"1","default_locale":"en"}""",
            new() { ["en"] = "not json" });
        Assert.Equal(new[] { ExtensionFixture.Id('b'), ExtensionFixture.Id('c') }.ToHashSet(), f.Inventory.Load().Select(i => i.Name).ToHashSet());
    }

    [Fact]
    public void SkipsVersionsWithoutManifestAndNonProfileFolders()
    {
        using var f = new ExtensionFixture();
        f.Chromium(Chrome, "Default", ExtensionFixture.Id('d'), "1_0", null);
        f.Chromium(Chrome, "System Profile", ExtensionFixture.Id('e'), "1_0", """{"name":"Sys"}""");
        f.Chromium(Chrome, "Guest Profile", ExtensionFixture.Id('f'), "1_0", """{"name":"Guest"}""");
        f.Chromium(Chrome, "Default", ExtensionFixture.Id('g'), "1_0", "{{{");
        f.Chromium(Chrome, "Default", "Temp", "1_0", """{"name":"NotAnId"}""");
        Assert.Empty(f.Inventory.Load());
    }

    [Fact]
    public void OnlyExtensionsTheUserAddedFromTheStoreAreRemovable()
    {
        using var f = new ExtensionFixture();
        foreach (var c in "hijkl") f.Chromium(Chrome, "Default", ExtensionFixture.Id(c), "1_0", $$"""{"name":"{{c}}","version":"1"}""");
        f.Settings(Chrome, "Default", $$"""
            {"{{ExtensionFixture.Id('h')}}":{"location":1,"from_webstore":true},
             "{{ExtensionFixture.Id('i')}}":{"location":1,"was_installed_by_default":true},
             "{{ExtensionFixture.Id('j')}}":{"location":9},
             "{{ExtensionFixture.Id('k')}}":{"location":3} }
            """);
        f.Settings(Chrome, "Default", $$"""{"{{ExtensionFixture.Id('l')}}":{"location":1} }""", file: "Preferences");
        var byName = f.Inventory.Load().ToDictionary(i => i.Name);
        Assert.True(byName["h"].IsRemovable);
        Assert.True(byName["l"].IsRemovable);
        Assert.False(byName["i"].IsRemovable);
        Assert.Equal("pré-instalada pelo navegador", byName["i"].Detail);
        Assert.False(byName["j"].IsRemovable);
        Assert.Equal("instalada por política", byName["j"].Detail);
        Assert.False(byName["k"].IsRemovable);
    }

    [Fact]
    public void UnpackedExtensionsAreListedButNeverRemovable()
    {
        using var f = new ExtensionFixture();
        var dev = f.T.Dir(@"source\my-extension");
        File.WriteAllText(Path.Combine(dev, "manifest.json"), """{"name":"Minha extensão","version":"0.1"}""");
        f.Settings(Chrome, "Default", $$"""{"{{ExtensionFixture.Id('m')}}":{"location":4,"path":{{System.Text.Json.JsonSerializer.Serialize(dev)}}} }""");
        var item = Assert.Single(f.Inventory.Load());
        Assert.Equal("Minha extensão", item.Name);
        Assert.Equal(dev, item.Path);
        Assert.False(item.IsRemovable);
    }

    [Fact]
    public void ListsFirefoxProfileExtensionsAndHidesBuiltIns()
    {
        using var f = new ExtensionFixture();
        var profile = f.T.Dir(Path.Combine(f.Roaming, @"Mozilla\Firefox\Profiles\ab12cd.default-release"));
        var xpi = f.T.File(Path.Combine(profile, "extensions", "uBlock0@raymondhill.net.xpi"), 300);
        File.WriteAllText(Path.Combine(profile, "extensions.json"), $$"""
            {"addons":[
              {"id":"uBlock0@raymondhill.net","type":"extension","location":"app-profile","version":"1.60.0",
               "defaultLocale":{"name":"uBlock Origin"},"path":{{System.Text.Json.JsonSerializer.Serialize(xpi)}}},
              {"id":"formautofill@mozilla.org","type":"extension","location":"app-builtin","defaultLocale":{"name":"Form Autofill"} },
              {"id":"pt-BR@dictionaries","type":"dictionary","location":"app-profile","defaultLocale":{"name":"Dicionário"} },
              {"id":"global@vendor","type":"extension","location":"app-system-share","defaultLocale":{"name":"Vendor Helper"} }
            ]}
            """);
        var items = f.Inventory.Load().ToDictionary(i => i.Name);
        Assert.Equal(["uBlock Origin", "Vendor Helper"], items.Keys.Order());
        var ublock = items["uBlock Origin"];
        Assert.Equal(("Firefox", "default-release", "1.60.0", xpi), (ublock.Browser, ublock.Profile, ublock.Version, ublock.Path));
        Assert.True(ublock.IsRemovable);
        Assert.False(items["Vendor Helper"].IsRemovable);
    }

    [Fact]
    public void ScreenSaversInUserFoldersAreUserScopeAndSystemOnesAreNot()
    {
        using var f = new ExtensionFixture();
        f.T.File(Path.Combine(f.T.Windows, "System32", "Bubbles.scr"));
        f.T.File(Path.Combine(f.T.Windows, "SysWOW64", "Bubbles.scr"));
        var mine = f.T.File(Path.Combine(f.LocalAppData, "Programs", "Aerial", "Aerial.scr"));
        f.T.File(Path.Combine(f.T.Home, "Downloads", "notes.txt"));
        var items = f.Inventory.Load().Where(i => i.Kind == ExtensionKind.ScreenSaver).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(ExtensionScope.System, items.Single(i => i.Name == "Bubbles").Scope);
        var aerial = items.Single(i => i.Name == "Aerial");
        Assert.Equal((ExtensionScope.User, mine), (aerial.Scope, aerial.Path));
        Assert.True(aerial.IsRemovable);
        Assert.False(items.Single(i => i.Name == "Bubbles").IsRemovable);
    }

    [Fact]
    public void OfficeAddInFilesAreReportedOnly()
    {
        using var f = new ExtensionFixture();
        f.T.File(Path.Combine(f.Roaming, @"Microsoft\AddIns\Tools.xlam"));
        f.T.File(Path.Combine(f.Roaming, @"Microsoft\Word\STARTUP\Macros.dotm"));
        f.T.File(Path.Combine(f.Roaming, @"Microsoft\AddIns\readme.txt"));
        var items = f.Inventory.Load();
        Assert.Equal(["Macros", "Tools"], items.Select(i => i.Name).Order());
        Assert.All(items, i => Assert.False(i.IsRemovable));
    }

    [Fact]
    public void JunctionedProfilesAreNotWalked()
    {
        using var f = new ExtensionFixture();
        var elsewhere = f.T.Dir(Path.Combine(f.T.Root, "elsewhere"));
        f.T.Dir(Path.Combine(elsewhere, "Extensions", ExtensionFixture.Id('n'), "1_0"));
        File.WriteAllText(Path.Combine(elsewhere, "Extensions", ExtensionFixture.Id('n'), "1_0", "manifest.json"), """{"name":"Linked"}""");
        var userData = f.T.Dir(Path.Combine(f.LocalAppData, Chrome));
        try { Directory.CreateSymbolicLink(Path.Combine(userData, "Default"), elsewhere); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // symlinks need Developer Mode
        Assert.Empty(f.Inventory.Load());
    }
}

public class ExtensionRemovalTests
{
    private const string Chrome = @"Google\Chrome\User Data";

    private static (ExtensionFixture F, InstalledExtension Item) UserChromeExtension()
    {
        var f = new ExtensionFixture();
        var id = ExtensionFixture.Id('h');
        f.Chromium(Chrome, "Default", id, "1_0", """{"name":"Mine","version":"1"}""");
        f.Settings(Chrome, "Default", $$"""{"{{id}}":{"location":1} }""");
        return (f, f.Inventory.Load().Single());
    }

    [Fact]
    public void RemovableExtensionsBecomeRecyclableScanItems()
    {
        var (f, item) = UserChromeExtension();
        using var _ = f;
        var plan = ExtensionRemoval.Make([item], new HashSet<string>(), f.T.Environment);
        Assert.Empty(plan.Refused);
        var remover = new FakeRemover();
        var result = new Cleaner(f.T.Policy(), remover).Clean(plan.Files);
        Assert.Empty(result.Failed);
        Assert.Equal([item.Path!], remover.Recycled);
    }

    [Fact]
    public void BrowserExtensionsAreRefusedWhileTheBrowserRuns()
    {
        var (f, item) = UserChromeExtension();
        using var _ = f;
        var plan = ExtensionRemoval.Make([item], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHROME" }, f.T.Environment);
        Assert.Empty(plan.Files);
        Assert.Equal("feche o Chrome antes de remover", Assert.Single(plan.Refused).Reason);
    }

    [Fact]
    public void ReportOnlyItemsAndForgedPathsAreRefused()
    {
        var (f, item) = UserChromeExtension();
        using var _ = f;
        InstalledExtension[] forged =
        [
            item with { Path = Path.GetDirectoryName(item.Path) }, // the Extensions folder itself
            item with { Path = f.T.Dir("Documents") },
            item with { Scope = ExtensionScope.System },
            item with { Browser = "Netscape" },
            new() { Kind = ExtensionKind.ScreenSaver, Scope = ExtensionScope.User, Name = "x", Path = Path.Combine(f.T.Windows, "System32", "x.scr") },
            new() { Kind = ExtensionKind.OfficeAddIn, Scope = ExtensionScope.User, Name = "o", Path = f.T.File(@"AppData\Roaming\Microsoft\AddIns\o.xlam") },
            new() { Kind = ExtensionKind.ShellExtension, Scope = ExtensionScope.User, Name = "s", RegistryKeys = [@"HKLM\Software\Classes\*\shellex\ContextMenuHandlers\X"] },
        ];
        var plan = ExtensionRemoval.Make(forged, new HashSet<string>(), f.T.Environment);
        Assert.Empty(plan.Files);
        Assert.Empty(plan.RegistryItems);
        Assert.Equal(forged.Length, plan.Refused.Count);
    }

    [Fact]
    public void UserShellExtensionsGoToTheRegistryRemover()
    {
        using var t = new TempHome();
        var item = new InstalledExtension
        {
            Kind = ExtensionKind.ShellExtension, Scope = ExtensionScope.User, Name = "7-Zip",
            RegistryKeys = [@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\7-Zip", @"HKCU\Software\Classes\Directory\shellex\ContextMenuHandlers\7-Zip"],
        };
        Assert.True(item.IsRemovable);
        var plan = ExtensionRemoval.Make([item], new HashSet<string>(), t.Environment);
        Assert.Equal([item], plan.RegistryItems);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\7-Zip", true)]
    [InlineData(@"HKCU\Software\Classes\Directory\Background\shellex\ContextMenuHandlers\X", true)]
    [InlineData(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers", false)]
    [InlineData(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\", false)]
    [InlineData(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\X\Sub", false)]
    [InlineData(@"HKCU\Software\Microsoft\Windows", false)]
    [InlineData(@"HKLM\Software\Classes\*\shellex\ContextMenuHandlers\X", false)]
    [InlineData(@"HKCU\Software\Classes\..\shellex\ContextMenuHandlers\X", false)]
    public void OnlyUserContextMenuHandlerKeysAreRemovable(string key, bool expected) =>
        Assert.Equal(expected, ExtensionRemoval.IsUserShellExtensionKey(key));
}
