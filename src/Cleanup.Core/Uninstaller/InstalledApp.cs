using System.Text.RegularExpressions;
using Cleanup.Core.Lists;
using Cleanup.Core.Models;

namespace Cleanup.Core.Uninstaller;

public enum AppKind
{
    /// <summary>A classic program, listed under a registry <c>Uninstall</c> key.</summary>
    Win32,
    /// <summary>An MSIX / Microsoft Store package.</summary>
    Store,
}

/// <summary>
/// An installed application (the Swift <c>InstalledApp</c>). Win32 apps come from the registry <c>Uninstall</c> keys,
/// Store apps from <c>PackageManager</c> (design §5.2).
/// </summary>
public sealed record InstalledApp
{
    /// <summary>"win32:HKLM64:{key}" or "store:{package full name}". Stable across reloads.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppKind Kind { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public long Size { get; init; }

    /// <summary>The app's own folder (Program Files, LocalAppData\Programs, WindowsApps…), when known.</summary>
    public string? InstallFolder { get; init; }
    /// <summary>The main executable (from <c>DisplayIcon</c>), when it is one.</summary>
    public string? MainExecutable { get; init; }

    /// <summary>Win32: the full path of the <c>Uninstall</c> subkey ("HKEY_LOCAL_MACHINE\SOFTWARE\…\{GUID}").</summary>
    public string? RegistryKey { get; init; }
    /// <summary>Win32: 64 or 32 (the registry view the key was read from).</summary>
    public int RegistryView { get; init; } = 64;
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }

    /// <summary>Store: identity used by <c>PackageManager</c>.</summary>
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }

    /// <summary>Windows components and apps without an uninstaller: never offered for removal or reset.</summary>
    public bool IsSystemApp { get; init; }

    public string KindLabel => Kind == AppKind.Store ? "Microsoft Store" : "Programa";
}

/// <summary>Builds <see cref="InstalledApp"/> values from raw registry values (pure, so tests can feed fake keys).</summary>
public static partial class AppInventory
{
    /// <summary>Release types Windows lists under Uninstall for patches, never as apps.</summary>
    private static readonly string[] UpdateReleaseTypes = ["Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup"];

    /// <summary>
    /// One <c>Uninstall</c> subkey → an app, or null for what Settings › Apps hides too: <c>SystemComponent=1</c>, no
    /// <c>DisplayName</c>, updates (<c>ParentKeyName</c> / <c>ReleaseType</c>) and KB patches.
    /// </summary>
    /// <param name="keyPath">Full key path, e.g. <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo</c>.</param>
    /// <param name="source">"HKLM64", "HKLM32" or "HKCU": part of the id.</param>
    public static InstalledApp? FromUninstallKey(string keyPath, string source, int view, IReadOnlyDictionary<string, object?> values,
        ScanEnvironment environment)
    {
        string? Str(string name) => values.TryGetValue(name, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        long? Num(string name) => values.TryGetValue(name, out var v) ? v switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s, out var p) => p,
            _ => null,
        } : null;

        var name = Str("DisplayName");
        if (name is null || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return null;
        if (Num("SystemComponent") == 1) return null;
        if (Str("ParentKeyName") is not null) return null;
        if (Str("ReleaseType") is { } release && UpdateReleaseTypes.Any(r => r.Equals(release, StringComparison.OrdinalIgnoreCase))) return null;
        if (KbPatch().IsMatch(name)) return null;

        var uninstall = Str("UninstallString");
        var quiet = Str("QuietUninstallString");
        var mainExe = ExecutableFromIcon(Str("DisplayIcon"));
        var folder = SpecificFolder(Str("InstallLocation"), environment)
                     ?? (mainExe is null ? null : SpecificFolder(Path.GetDirectoryName(mainExe), environment));
        var estimatedKb = Num("EstimatedSize");
        var publisher = Str("Publisher");
        var keyName = keyPath[(keyPath.LastIndexOf('\\') + 1)..];
        return new InstalledApp
        {
            Id = $"win32:{source}:{keyName}".ToLowerInvariant(),
            Name = name,
            Kind = AppKind.Win32,
            Publisher = publisher,
            Version = Str("DisplayVersion"),
            Size = estimatedKb is > 0 ? estimatedKb.Value * 1024 : 0,
            InstallFolder = folder,
            MainExecutable = mainExe,
            RegistryKey = keyPath,
            RegistryView = view,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            // No uninstaller, or marked as not removable: Settings won't remove it either.
            IsSystemApp = (uninstall is null && quiet is null) || Num("NoRemove") == 1 || IsWindowsPublisher(publisher),
        };
    }

    /// <summary>Windows' own components (publisher "Microsoft Windows"), never removed or reset.</summary>
    public static bool IsWindowsPublisher(string? publisher) =>
        publisher is not null && (publisher.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
                                  || publisher.StartsWith("CN=Microsoft Windows", StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops repeats (the same app under the 64- and 32-bit views, or HKLM and HKCU) and sorts by name.</summary>
    public static List<InstalledApp> Merge(IEnumerable<InstalledApp> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return apps.Where(a => seen.Add(a.Name + "\u0001" + a.Version + "\u0001" + a.Kind))
            .OrderBy(a => SearchFilter.Fold(a.Name), StringComparer.Ordinal).ToList();
    }

    /// <summary><c>"C:\App\app.exe",0</c> → <c>C:\App\app.exe</c>; null for icons that aren't executables.</summary>
    public static string? ExecutableFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            s = end < 0 ? s[1..] : s[1..end];
        }
        else
        {
            var comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        }
        s = s.Trim();
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(s) ? s : null;
    }

    /// <summary>
    /// A folder that belongs to one app: absolute, at least two levels below the drive root and not a shared system
    /// location (the Windows folder, Program Files itself, ProgramData's package cache). Anything else is null, so a
    /// broken <c>InstallLocation</c> like <c>C:\Program Files</c> never makes every program look like this app.
    /// </summary>
    public static string? SpecificFolder(string? folder, ScanEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = folder.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(f) || f.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try { f = PathUtil.Normalize(f); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var root = Path.GetPathRoot(f) ?? "";
        var depth = f[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2) return null;
        if (Safety.SafetyPolicy.IsWithin(f, environment.SystemRoot)) return null;
        if (Safety.SafetyPolicy.IsWithin(f, Path.Combine(environment.ProgramData, "Package Cache"))) return null;
        string[] shared =
        [
            environment.Variable("ProgramFiles") ?? Path.Combine(root, "Program Files"),
            environment.Variable("ProgramFiles(x86)") ?? Path.Combine(root, "Program Files (x86)"),
            environment.Variable("CommonProgramFiles") ?? Path.Combine(root, "Program Files", "Common Files"),
            environment.ProgramData, environment.LocalAppData, environment.RoamingAppData,
            Path.Combine(environment.LocalAppData, "Programs"), environment.Home,
        ];
        return shared.Any(s => PathUtil.SameOrdinalIgnoreCase(f, s)) ? null : f;
    }

    [GeneratedRegex(@"^(Security Update|Update|Hotfix) for |\(KB\d{6,}\)|^KB\d{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex KbPatch();
}
