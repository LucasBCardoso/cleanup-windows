using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Uninstaller;

/// <summary>The registry, read-only, behind an interface so tests can fake <c>HKCU\Software</c>.</summary>
public interface IRegistryBrowser
{
    /// <summary>Names of the subkeys of a full key path ("HKEY_CURRENT_USER\Software\Vendor"); empty when missing.</summary>
    IReadOnlyList<string> SubKeyNames(string key);

    /// <summary>Roughly how much the key holds (names and data of its values and subkeys), for display.</summary>
    long ApproximateSize(string key);
}

/// <summary>What an app left behind: removable items (user folders, Start Menu, <c>HKCU</c> keys) and items in
/// system folders that are only reported (ProgramData, Program Files, the all-users Start Menu need admin).</summary>
public sealed record Leftovers(IReadOnlyList<ScanItem> Removable, IReadOnlyList<ScanItem> ReportOnly)
{
    public static readonly Leftovers None = new([], []);
}

/// <summary>
/// Finds what a Windows app scattered around the profile (the Swift <c>LeftoverFinder</c>): folders named after the
/// product, or <c>Vendor\Product</c>, in <c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>, <c>LocalLow</c> and
/// <c>%LOCALAPPDATA%\Programs</c>; its Start Menu entries; and <c>HKCU\Software\Vendor\Product</c>. A name that another
/// installed app also answers to is never offered (two versions of Python share their folders).
/// </summary>
public sealed class LeftoverFinder(ScanEnvironment environment, SafetyPolicy policy, IRegistryBrowser? registry = null)
{
    public const string FileRule = "app-leftover";
    public const string RegistryRule = "registry-leftover";
    public const string ReportRule = "app-leftover-report";

    public ScanEnvironment Environment => environment;
    public SafetyPolicy Policy => policy;

    public string Roaming => environment.RoamingAppData;
    public string Local => environment.LocalAppData;
    public string LocalLow => Path.Combine(environment.Home, "AppData", "LocalLow");
    public string UserPrograms => Path.Combine(environment.LocalAppData, "Programs");
    public string StartMenu => Path.Combine(environment.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs");
    public string CommonStartMenu => Path.Combine(environment.ProgramData, @"Microsoft\Windows\Start Menu\Programs");

    public Leftovers Find(InstalledApp app, IReadOnlyList<InstalledApp> installed)
    {
        // Store apps keep everything in their package folder, which Windows removes with the package.
        if (app.Kind == AppKind.Store || app.IsSystemApp) return Leftovers.None;
        var names = new Names(app, installed);
        if (names.Products.Count == 0) return Leftovers.None;

        var removable = new List<ScanItem>();
        var report = new List<ScanItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, bool reportOnly)
        {
            if (!seen.Add(path) || IsLink(path)) return;
            // A user folder the policy refuses (ignored, protected) is simply not offered, as on macOS.
            var allowed = !reportOnly && policy.Verdict(path).IsAllowed;
            if (!reportOnly && !allowed) return;
            var item = new ScanItem
            {
                Path = path, Size = FileMetrics.Size(path), RuleId = allowed ? FileRule : ReportRule,
                Category = ScanCategory.AppLeftovers, Risk = RiskLevel.Safe,
            };
            (allowed ? removable : report).Add(item);
        }

        foreach (var root in (string[])[Roaming, Local, LocalLow, UserPrograms])
        {
            foreach (var path in MatchedFolders(root, names)) Add(path, reportOnly: false);
        }
        foreach (var path in MatchedFolders(StartMenu, names, shortcuts: true)) Add(path, reportOnly: false);
        foreach (var path in MatchedFolders(environment.ProgramData, names)) Add(path, reportOnly: true);
        foreach (var path in MatchedFolders(CommonStartMenu, names, shortcuts: true)) Add(path, reportOnly: true);
        // The install folder, when the uninstaller leaves it: removable only in the per-user app folders. Anywhere else
        // (Program Files, or an InstallLocation that points somewhere odd) it is only reported.
        if (app.InstallFolder is { } folder && Directory.Exists(folder))
        {
            var userOwned = ((string[])[UserPrograms, Local, Roaming]).Any(r => SafetyPolicy.IsWithin(folder, r) && !PathUtil.SameOrdinalIgnoreCase(folder, r));
            Add(folder, reportOnly: !userOwned);
        }

        removable.AddRange(RegistryKeys(names));
        return new Leftovers(removable.OrderByDescending(i => i.Size).ToList(), report.OrderByDescending(i => i.Size).ToList());
    }

    /// <summary><c>HKCU\Software\Product</c> and <c>HKCU\Software\Vendor\Product</c>.</summary>
    internal IEnumerable<ScanItem> RegistryKeys(Names names)
    {
        if (registry is null) yield break;
        foreach (var top in registry.SubKeyNames(RegistryPolicy.UserSoftware))
        {
            var key = AppNames.Key(top);
            if (names.IsOwnProduct(key)) { if (Item(RegistryPolicy.UserSoftware + "\\" + top, top) is { } item) yield return item; }
            else if (names.VendorKeys.Contains(key))
            {
                var vendorKey = RegistryPolicy.UserSoftware + "\\" + top;
                foreach (var child in registry.SubKeyNames(vendorKey))
                {
                    if (names.IsOwnProduct(AppNames.Key(child)) && Item(vendorKey + "\\" + child, top + "\\" + child) is { } item) yield return item;
                }
            }
        }

        ScanItem? Item(string key, string display) => RegistryPolicy.IsAllowed(key) ? new ScanItem
        {
            Path = key, Size = registry.ApproximateSize(key), RuleId = RegistryRule, Category = ScanCategory.AppLeftovers,
            Risk = RiskLevel.Safe, DisplayName = display,
        } : null;
    }

    /// <summary>Folders (and, in the Start Menu, <c>.lnk</c> files) named <c>root\Product</c> or <c>root\Vendor\Product</c>.</summary>
    internal static IEnumerable<string> MatchedFolders(string root, Names names, bool shortcuts = false)
    {
        foreach (var entry in Children(root))
        {
            var name = Path.GetFileName(entry);
            var isDir = Directory.Exists(entry);
            if (!isDir && !(shortcuts && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))) continue;
            var key = AppNames.Key(isDir ? name : Path.GetFileNameWithoutExtension(name));
            if (names.IsOwnProduct(key)) yield return entry;
            else if (isDir && names.VendorKeys.Contains(key))
            {
                foreach (var child in Children(entry))
                {
                    var childName = Path.GetFileName(child);
                    var childIsDir = Directory.Exists(child);
                    if (!childIsDir && !(shortcuts && childName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))) continue;
                    if (names.IsOwnProduct(AppNames.Key(childIsDir ? childName : Path.GetFileNameWithoutExtension(childName)))) yield return child;
                }
            }
        }
    }

    private static List<string> Children(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>An app's product and vendor keys, minus product names another installed app also answers to.</summary>
    internal sealed class Names
    {
        public HashSet<string> Products { get; }
        public HashSet<string> VendorKeys { get; }
        private readonly HashSet<string> shared;

        public Names(InstalledApp app, IReadOnlyList<InstalledApp> installed)
        {
            Products = AppNames.ProductNames(app).Select(AppNames.Key).ToHashSet(StringComparer.Ordinal);
            VendorKeys = AppNames.VendorNames(app).Select(AppNames.Key).ToHashSet(StringComparer.Ordinal);
            shared = installed.Where(o => o.Id != app.Id)
                .SelectMany(o => AppNames.ProductNames(o).Select(AppNames.Key))
                .ToHashSet(StringComparer.Ordinal);
            Products.ExceptWith(shared);
            // A vendor name that is also one of the product names ("Docker\Docker") stays a product match.
        }

        public bool IsOwnProduct(string key) => Products.Contains(key);
    }
}
