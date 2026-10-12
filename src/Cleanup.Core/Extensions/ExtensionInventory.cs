using System.Text.Json;
using System.Text.RegularExpressions;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;
using Cleanup.Core.Updater;

namespace Cleanup.Core.Extensions;

/// <summary>Kinds of add-ons the Extensões module lists (design §5.2), in display order.</summary>
public enum ExtensionKind
{
    ShellExtension,
    Browser,
    ScreenSaver,
    OfficeAddIn,
}

public static class ExtensionKindExtensions
{
    public static string DisplayName(this ExtensionKind kind) => kind switch
    {
        ExtensionKind.ShellExtension => "Extensões do Explorador",
        ExtensionKind.Browser => "Extensões de navegador",
        ExtensionKind.ScreenSaver => "Protetores de tela",
        _ => "Suplementos do Office",
    };
}

public enum ExtensionScope
{
    User,
    System,
}

/// <summary>
/// One installed add-on. Mirrors the Swift <c>InstalledExtension</c>. File-based items have a <see cref="Path"/>;
/// Explorer extensions and most Office add-ins live in the registry (<see cref="RegistryKeys"/>).
/// </summary>
public sealed record InstalledExtension
{
    public required ExtensionKind Kind { get; init; }
    public required ExtensionScope Scope { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public long Size { get; init; }
    /// <summary>The extension's folder or file on disk, when it has one.</summary>
    public string? Path { get; init; }
    public string? Browser { get; init; }
    public string? Profile { get; init; }
    /// <summary>A second line: the DLL, the registry location, "instalada por política"…</summary>
    public string? Detail { get; init; }
    /// <summary>The DLL behind a registry-based extension, for "Mostrar no Explorador".</summary>
    public string? DllPath { get; init; }
    /// <summary>Registry keys that register it (<c>HKCU\…</c> / <c>HKLM\…</c>), for Explorer extensions.</summary>
    public IReadOnlyList<string> RegistryKeys { get; init; } = [];
    /// <summary>
    /// For browser extensions: added by the user from the browser's store, not by policy, another program, the
    /// browser itself or as an unpacked development folder.
    /// </summary>
    public bool InstalledByUser { get; init; }

    public string? RevealPath => Path ?? DllPath;

    public string Id => $"{Kind}:{Scope}:{Path ?? RegistryKeys.FirstOrDefault() ?? Name}".ToLowerInvariant();

    /// <summary>
    /// Only what the user installed in their own profile: user screen savers, browser extensions they added, and
    /// Explorer extensions registered for this user only. System items, policy-installed and Office add-ins are reported.
    /// </summary>
    public bool IsRemovable => Scope == ExtensionScope.User && Kind switch
    {
        ExtensionKind.ScreenSaver => Path is not null,
        ExtensionKind.Browser => Path is not null && InstalledByUser,
        ExtensionKind.ShellExtension => RegistryKeys.Count > 0 && RegistryKeys.All(k => k.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>For removal through <c>Cleaner</c>, which re-checks <c>SafetyPolicy</c>. Null for registry-only items.</summary>
    public ScanItem? ScanItem => Path is null ? null : new ScanItem
    {
        Path = Path, Size = Size, RuleId = "extension-" + Kind.ToString().ToLowerInvariant(),
        Category = ScanCategory.AppLeftovers, Risk = RiskLevel.Review, DisplayName = Name,
    };
}

/// <summary>A Chromium-family browser: display name, <c>User Data</c> folder under %LOCALAPPDATA%, process name.</summary>
public sealed record ChromiumBrowser(string Name, string UserDataFolder, string ProcessName);

/// <summary>
/// Lists file-based add-ons: browser extensions per profile (Chromium family and Firefox), screen savers and Office
/// add-in files. Explorer extensions and registry Office add-ins come from Cleanup.Platform.Windows.
/// </summary>
public sealed partial class ExtensionInventory(ScanEnvironment environment, bool measureSize = true)
{
    public static IReadOnlyList<ChromiumBrowser> ChromiumBrowsers { get; } =
    [
        new("Chrome", @"Google\Chrome\User Data", "chrome"),
        new("Edge", @"Microsoft\Edge\User Data", "msedge"),
        new("Brave", @"BraveSoftware\Brave-Browser\User Data", "brave"),
        new("Vivaldi", @"Vivaldi\User Data", "vivaldi"),
    ];

    public const string FirefoxName = "Firefox";
    public const string FirefoxProcess = "firefox";

    /// <summary>Extra screen savers the platform knows about (the one set in Control Panel\Desktop).</summary>
    public IReadOnlyList<string> ExtraScreenSavers { get; init; } = [];

    public IReadOnlyList<InstalledExtension> Load() =>
        [.. ChromiumBrowsers.SelectMany(ChromiumExtensions), .. FirefoxExtensions(), .. ScreenSavers(), .. OfficeFileAddIns()];

    // MARK: Chromium

    internal IEnumerable<InstalledExtension> ChromiumExtensions(ChromiumBrowser browser)
    {
        var root = System.IO.Path.Combine(environment.LocalAppData, browser.UserDataFolder);
        foreach (var profile in Children(root).Where(p => IsProfile(System.IO.Path.GetFileName(p)) && IsRealDirectory(p)))
        {
            var profileName = System.IO.Path.GetFileName(profile);
            var settings = ChromiumSettings.Read(profile);
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Children(System.IO.Path.Combine(profile, "Extensions")))
            {
                var id = System.IO.Path.GetFileName(folder);
                if (!ChromiumId().IsMatch(id) || !IsRealDirectory(folder)) continue;
                if (NewestVersionFolder(folder) is not { } newest || ChromiumManifest.Read(newest) is not { } manifest) continue;
                listed.Add(id);
                settings.TryGetValue(id, out var setting);
                yield return new InstalledExtension
                {
                    Kind = ExtensionKind.Browser, Scope = ExtensionScope.User, Name = manifest.Name ?? id, Version = manifest.Version,
                    Size = SizeOf(folder), Path = folder, Browser = browser.Name, Profile = profileName,
                    InstalledByUser = setting?.IsUserInstalled ?? false, Detail = setting?.OriginNote ?? "origem desconhecida",
                };
            }
            // Unpacked (developer) extensions live in the user's own folder: listed, never removed from here.
            foreach (var (id, setting) in settings.Where(s => s.Value.Location == ChromiumSetting.Unpacked && !listed.Contains(s.Key)))
            {
                if (setting.UnpackedPath is not { } path || !System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path)) continue;
                var manifest = ChromiumManifest.Read(path);
                yield return new InstalledExtension
                {
                    Kind = ExtensionKind.Browser, Scope = ExtensionScope.User, Name = manifest?.Name ?? id, Version = manifest?.Version,
                    Size = 0, Path = path, Browser = browser.Name, Profile = profileName, InstalledByUser = false,
                    Detail = setting.OriginNote,
                };
            }
        }
    }

    internal static bool IsProfile(string name) => name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal);

    internal static string? NewestVersionFolder(string extensionFolder) =>
        Children(extensionFolder).Where(IsRealDirectory)
            .OrderBy(p => System.IO.Path.GetFileName(p), Comparer<string>.Create(VersionComparator.Compare))
            .LastOrDefault();

    // MARK: Firefox

    internal IEnumerable<InstalledExtension> FirefoxExtensions()
    {
        var root = System.IO.Path.Combine(environment.RoamingAppData, @"Mozilla\Firefox\Profiles");
        foreach (var profile in Children(root).Where(IsRealDirectory))
        {
            var file = System.IO.Path.Combine(profile, "extensions.json");
            if (!File.Exists(file)) continue;
            List<FirefoxAddon> addons;
            try { addons = FirefoxAddon.Parse(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            var folderName = System.IO.Path.GetFileName(profile);
            var profileName = folderName.Contains('.') ? folderName[(folderName.IndexOf('.') + 1)..] : folderName;
            foreach (var addon in addons.Where(a => a.Type == "extension" && a.IsListed))
            {
                var inProfile = addon.Path is { } p && System.IO.Path.IsPathFullyQualified(p)
                                && SafetyPolicy.IsWithin(p, System.IO.Path.Combine(profile, "extensions")) && (File.Exists(p) || Directory.Exists(p));
                yield return new InstalledExtension
                {
                    Kind = ExtensionKind.Browser, Scope = addon.Location == "app-profile" ? ExtensionScope.User : ExtensionScope.System,
                    Name = addon.Name ?? addon.Id, Version = addon.Version, Size = inProfile ? SizeOf(addon.Path!) : 0,
                    Path = inProfile ? addon.Path : null, Browser = FirefoxName, Profile = profileName,
                    InstalledByUser = addon.Location == "app-profile" && inProfile,
                    Detail = addon.Location == "app-profile" ? "da loja de complementos" : "instalada por outro programa",
                };
            }
        }
    }

    // MARK: Screen savers

    internal IEnumerable<InstalledExtension> ScreenSavers()
    {
        var windows = environment.SystemRoot;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] systemFolders = [System.IO.Path.Combine(windows, "System32"), System.IO.Path.Combine(windows, "SysWOW64"), windows];
        var programs = System.IO.Path.Combine(environment.LocalAppData, "Programs");
        string[] userFolders =
        [
            environment.Home, System.IO.Path.Combine(environment.Home, "Downloads"), environment.LocalAppData,
            environment.RoamingAppData, programs, .. Children(programs).Where(IsRealDirectory),
        ];
        foreach (var file in systemFolders.SelectMany(ScrFiles))
        {
            // System32 and SysWOW64 hold the same savers: list each name once.
            if (seen.Add(System.IO.Path.GetFileName(file))) yield return ScreenSaver(file, ExtensionScope.System);
        }
        var userSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in userFolders.SelectMany(ScrFiles).Concat(ExtraScreenSavers.Where(File.Exists)))
        {
            if (!System.IO.Path.IsPathFullyQualified(file) || SafetyPolicy.IsWithin(file, windows) || !userSeen.Add(PathUtil.Normalize(file))) continue;
            var withinHome = SafetyPolicy.IsWithin(file, environment.Home);
            yield return ScreenSaver(file, withinHome ? ExtensionScope.User : ExtensionScope.System);
        }
    }

    private static IEnumerable<string> ScrFiles(string folder) =>
        Children(folder).Where(f => f.EndsWith(".scr", StringComparison.OrdinalIgnoreCase) && IsRegularFile(f));

    private InstalledExtension ScreenSaver(string file, ExtensionScope scope)
    {
        string? description = null, version = null;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
            description = string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription.Trim();
            version = string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { }
        return new InstalledExtension
        {
            Kind = ExtensionKind.ScreenSaver, Scope = scope, Name = description ?? System.IO.Path.GetFileNameWithoutExtension(file),
            Version = scope == ExtensionScope.System ? null : version, Size = SizeOf(file), Path = file,
        };
    }

    // MARK: Office add-in files (reported only)

    internal IEnumerable<InstalledExtension> OfficeFileAddIns()
    {
        var roaming = environment.RoamingAppData;
        (string Folder, string App, string[] Extensions)[] places =
        [
            (@"Microsoft\AddIns", "Office", [".xlam", ".xla", ".xll", ".ppam", ".ppa"]),
            (@"Microsoft\Word\STARTUP", "Word", [".dotm", ".dot", ".wll"]),
            (@"Microsoft\Excel\XLSTART", "Excel", [".xlam", ".xla", ".xlsm", ".xlsb", ".xltm"]),
        ];
        foreach (var (folder, app, extensions) in places)
        {
            foreach (var file in Children(System.IO.Path.Combine(roaming, folder))
                         .Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && IsRegularFile(f)))
            {
                yield return new InstalledExtension
                {
                    Kind = ExtensionKind.OfficeAddIn, Scope = ExtensionScope.User, Name = System.IO.Path.GetFileNameWithoutExtension(file),
                    Size = SizeOf(file), Path = file, Detail = app + " · arquivo de suplemento",
                };
            }
        }
    }

    // MARK: Helpers

    internal static IEnumerable<string> Children(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(p => !System.IO.Path.GetFileName(p).StartsWith('.'))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>A directory that isn't a junction or symlink (invariant 13).</summary>
    internal static bool IsRealDirectory(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            return a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool IsRegularFile(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            return !a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private long SizeOf(string path) => measureSize ? FileMetrics.Size(path) : 0;

    [GeneratedRegex("^[a-p]{32}$")]
    internal static partial Regex ChromiumId();
}

/// <summary>A Chromium extension's <c>manifest.json</c>, with <c>__MSG_key__</c> names resolved from <c>_locales</c>.</summary>
public sealed record ChromiumManifest(string? Name, string? Version)
{
    public static ChromiumManifest? Read(string versionFolder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(versionFolder, "manifest.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var raw = Text(doc.RootElement, "name");
            var locale = Text(doc.RootElement, "default_locale");
            return new ChromiumManifest(raw is null ? null : Resolve(raw, locale, versionFolder), Text(doc.RootElement, "version"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static string? Resolve(string name, string? locale, string folder)
    {
        if (!(name.StartsWith("__MSG_", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal) && name.Length > 8))
            return name.Length == 0 ? null : name;
        var key = name[6..^2];
        if (locale is null || locale.Contains('/') || locale.Contains('\\') || locale.Contains("..")) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "_locales", locale, "messages.json")));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                return Text(entry.Value, "message") is { Length: > 0 } message ? message : null;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// How a Chromium extension got into the profile, from <c>extensions.settings</c> in <c>Preferences</c> and
/// <c>Secure Preferences</c> (Chromium's <c>ManifestLocation</c>).
/// </summary>
public sealed record ChromiumSetting(int Location, bool InstalledByDefault, string? UnpackedPath)
{
    public const int Internal = 1, ExternalPref = 2, ExternalRegistry = 3, Unpacked = 4, Component = 5,
        ExternalPrefDownload = 6, ExternalPolicyDownload = 7, CommandLine = 8, ExternalPolicy = 9, ExternalComponent = 10;

    /// <summary>Added by the user from the web store (not preinstalled by the browser or an OEM).</summary>
    public bool IsUserInstalled => Location == Internal && !InstalledByDefault;

    public string OriginNote => Location switch
    {
        Internal when InstalledByDefault => "pré-instalada pelo navegador",
        Internal => "da loja de extensões",
        Unpacked or CommandLine => "descompactada (pasta de desenvolvimento)",
        ExternalPolicy or ExternalPolicyDownload => "instalada por política",
        Component or ExternalComponent => "parte do navegador",
        _ => "instalada por outro programa",
    };
}

internal static class ChromiumSettings
{
    public static Dictionary<string, ChromiumSetting> Read(string profile)
    {
        var result = new Dictionary<string, ChromiumSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (string[])["Preferences", "Secure Preferences"])
        {
            try
            {
                var path = System.IO.Path.Combine(profile, name);
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) || ext.ValueKind != JsonValueKind.Object
                    || !ext.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in settings.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.Value.TryGetProperty("location", out var loc) || !loc.TryGetInt32(out var location)) continue;
                    var byDefault = Bool(entry.Value, "was_installed_by_default") || Bool(entry.Value, "was_installed_by_oem");
                    result[entry.Name] = new ChromiumSetting(location, byDefault, ChromiumManifest.Text(entry.Value, "path"));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    private static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>One entry of Firefox's <c>extensions.json</c>.</summary>
internal sealed record FirefoxAddon(string Id, string? Type, string? Location, string? Name, string? Version, string? Path, bool Hidden)
{
    /// <summary>Firefox's own built-in and system add-ons aren't shown (they're part of the browser).</summary>
    public bool IsListed => !Hidden && Location is not ("app-builtin" or "app-system-defaults" or "app-system-addons" or "app-temporary");

    public static List<FirefoxAddon> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<FirefoxAddon>();
        if (!doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in addons.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || ChromiumManifest.Text(a, "id") is not { } id) continue;
            string? name = null;
            if (a.TryGetProperty("defaultLocale", out var locale) && locale.ValueKind == JsonValueKind.Object) name = ChromiumManifest.Text(locale, "name");
            var hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
            list.Add(new FirefoxAddon(id, ChromiumManifest.Text(a, "type"), ChromiumManifest.Text(a, "location"), name,
                ChromiumManifest.Text(a, "version"), ChromiumManifest.Text(a, "path"), hidden));
        }
        return list;
    }
}
